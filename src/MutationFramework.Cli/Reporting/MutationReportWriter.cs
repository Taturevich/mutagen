using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MutationFramework.Sdk;

internal static class MutationReportWriter
{
  internal static string Html(MutationRunResult result) => HtmlHeader(result).Append(HtmlRows(result)).ToString();

  private static StringBuilder HtmlHeader(MutationRunResult r)
  {
    static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
    var t = r.Timings;
    var c = r.Counters;
    var b = new StringBuilder("<!doctype html><meta charset=utf-8><title>Mutation report</title><style>body{font:15px system-ui;max-width:1100px;margin:2rem auto}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:.5rem;text-align:left}code{white-space:pre-wrap}.Killed{color:#a00}.Survived{color:#087}.Unsupported{color:#765}</style>");
    b.Append($"<h1>Mutation report</h1><p>Status: {E(r.OverallStatus)} | Score: {(r.MutationScore?.ToString("F1") + "%" ?? "n/a")} | Total: {r.Mutants.Count} | Killed: {r.KilledCount} | Survived: {r.SurvivedCount} | No coverage: {r.NoCoverageCount} | Unsupported: {r.UnsupportedMutations.Count} | Total duration: {r.TotalDurationMs} ms</p><h2>Exclusive phase timings (ms)</h2><p>Project evaluation {t.ProjectEvaluationMs}; source discovery {t.DiscoveryMs}; source copy {t.ProjectCopyMs}; Roslyn/instrumentation {t.InstrumentationMs}; build {t.BuildMs}; baseline tests {t.BaselineMs}; probe serialization {t.ProbeSerializationMs}; coverage mapping {t.CoverageMappingMs}; mutation generation {t.MutationGenerationMs}; planning {t.MutationPlanningMs}; worker initialization {t.WorkerInitializationMs}; mutation execution {Math.Max(0, t.ExecutionMs - t.WorkerInitializationMs)}; reporting {t.ReportingMs}.</p><p>Execution mode: {E(r.ExecutionPlan?.SelectedMode.ToString() ?? "not executed")}; planned batches: {r.ExecutionPlan?.Batches.Count ?? 0}; conflict edges: {r.ExecutionPlan?.Statistics.ExactConflictEdges ?? 0}.</p><h2>Execution counters</h2><p>Mutants: {c.TotalMutants} total, {c.CoveredMutants} covered, {c.UncoveredMutants} uncovered; tests {c.TotalTests}; baseline executions {c.BaselineTestExecutions}; mutation executions {c.MutationTestExecutions}; controller/test runner starts {c.TestRunnerProcessStarts}; testhost starts {c.TestHostProcessStarts}; discovery operations {c.TestDiscoveryOperations}; builds {c.BuildInvocations}; worker restarts {c.WorkerRestarts}; worker failures {c.WorkerFailures}; execution units {c.MutationBatches}, average mutants/unit {c.AverageMutantsPerBatch:F2}; worker busy {c.TotalWorkerBusyTimeMs} ms, idle {c.TotalWorkerIdleTimeMs} ms; observed process starts {r.ProcessStarts}.</p><table><thead><tr><th>Status</th><th>Source</th><th>Operator</th><th>Transformation</th><th>Tests</th></tr></thead><tbody>");
    return b;
  }

  private static string HtmlRows(MutationRunResult r)
  {
    static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
    var b = new StringBuilder();
    foreach (var m in r.Mutants)
    {
      b.Append($"<tr><td class='{m.Status}'>{m.Status}</td><td>{E(m.SourceFile)}:{m.Line}:{m.Column}</td><td>{E(m.Operator)}</td><td><code>{E(m.Original)} &#8594; {E(m.Replacement)}</code></td><td>{E(string.Join(", ", m.CoveringTests))}</td></tr>");
    }
    foreach (var m in r.UnsupportedMutations)
    {
      b.Append($"<tr><td class='Unsupported'>Unsupported</td><td>{E(m.SourceFile)}:{m.Line}:{m.Column}</td><td>{E(m.Operator)}</td><td><code>{E(m.Original)} &#8594; {E(m.Replacement)}</code></td><td>{E(m.Diagnostic ?? "")}</td></tr>");
    }
    return b.Append("</tbody></table>").ToString();
  }

  internal static async Task<MutationRunResult> WriteAsync(MutationRunResult result, string reports,
      System.Diagnostics.Stopwatch runTimer, CancellationToken ct)
  {
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var selectedReports = reports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (selectedReports.Count == 0 || selectedReports.Any(format => format is not ("json" or "html") &&
        !format.Equals("json", StringComparison.OrdinalIgnoreCase) && !format.Equals("html", StringComparison.OrdinalIgnoreCase)))
    {
      throw new ArgumentException("Report formats must be json, html, or json,html.", nameof(reports));
    }

    // Finalize timings after row preparation and main JSON serialization. The boundary
    // excludes final metadata serialization, HTML file I/O, flushing and publication.
    // This avoids recursively measuring the serialization of its own elapsed duration.
    var staged = new List<(string Temporary, string Destination)>();
    try
    {
      ct.ThrowIfCancellationRequested();
      var htmlRows = selectedReports.Contains("html") ? HtmlRows(result) : null;
      if (selectedReports.Contains("json"))
      {
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var temporary = StagePath(result.JsonReportPath);
        staged.Add((temporary, result.JsonReportPath));
        await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        foreach (var property in jsonOptions.GetTypeInfo(typeof(MutationRunResult)).Properties)
        {
          if (property.Name is nameof(MutationRunResult.Timings) or nameof(MutationRunResult.TotalDurationMs))
          {
            continue;
          }
          ct.ThrowIfCancellationRequested();
          writer.WritePropertyName(property.Name);
          JsonSerializer.Serialize(writer, property.Get!(result), property.PropertyType, jsonOptions);
        }
        result = FinalizeTiming(result, timer, runTimer);
        writer.WritePropertyName(nameof(MutationRunResult.Timings));
        JsonSerializer.Serialize(writer, result.Timings, jsonOptions);
        writer.WriteNumber(nameof(MutationRunResult.TotalDurationMs), result.TotalDurationMs);
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
        await stream.FlushAsync(ct);
      }
      else
      {
        result = FinalizeTiming(result, timer, runTimer);
      }
      if (htmlRows is not null)
      {
        var temporary = StagePath(result.HtmlReportPath);
        staged.Add((temporary, result.HtmlReportPath));
        await File.WriteAllTextAsync(temporary, HtmlHeader(result).Append(htmlRows).ToString(), ct);
      }
      // Stage every selected format before publishing any of them. Each rename is atomic;
      // a multi-file transaction is intentionally not promised by the report contract.
      ct.ThrowIfCancellationRequested();
      foreach (var (temporary, destination) in staged)
      {
        File.Move(temporary, destination, overwrite: true);
      }
      return result;
    }
    finally
    {
      foreach (var (temporary, _) in staged)
      {
        if (File.Exists(temporary))
        {
          File.Delete(temporary);
        }
      }
    }
  }

  private static MutationRunResult FinalizeTiming(MutationRunResult result, System.Diagnostics.Stopwatch reportingTimer,
      System.Diagnostics.Stopwatch runTimer) => result with
      {
        Timings = result.Timings with
        {
          ReportingMs = reportingTimer.ElapsedMilliseconds
        },
        TotalDurationMs = runTimer.ElapsedMilliseconds
      };

  private static string StagePath(string destination) =>
      Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
}
