using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace MutationFramework.Sdk;

internal static class MutationReportWriter
{
  internal static string Html(MutationRunResult r)
  {
    static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
    var t = r.Timings;
    var c = r.Counters;
    var b = new StringBuilder("<!doctype html><meta charset=utf-8><title>Mutation report</title><style>body{font:15px system-ui;max-width:1100px;margin:2rem auto}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:.5rem;text-align:left}code{white-space:pre-wrap}.Killed{color:#a00}.Survived{color:#087}.Unsupported{color:#765}</style>");
    b.Append($"<h1>Mutation report</h1><p>Status: {E(r.OverallStatus)} | Score: {(r.MutationScore?.ToString("F1") + "%" ?? "n/a")} | Total: {r.Mutants.Count} | Killed: {r.KilledCount} | Survived: {r.SurvivedCount} | No coverage: {r.NoCoverageCount} | Unsupported: {r.UnsupportedMutations.Count} | Total duration: {r.TotalDurationMs} ms</p><h2>Exclusive phase timings (ms)</h2><p>Project evaluation {t.ProjectEvaluationMs}; source discovery {t.DiscoveryMs}; source copy {t.ProjectCopyMs}; Roslyn/instrumentation {t.InstrumentationMs}; build {t.BuildMs}; baseline tests {t.BaselineMs}; probe serialization {t.ProbeSerializationMs}; coverage mapping {t.CoverageMappingMs}; mutation generation {t.MutationGenerationMs}; planning {t.MutationPlanningMs}; worker initialization {t.WorkerInitializationMs}; mutation execution {Math.Max(0, t.ExecutionMs - t.WorkerInitializationMs)}; reporting {t.ReportingMs}.</p><p>Execution mode: {E(r.ExecutionPlan?.SelectedMode.ToString() ?? "not executed")}; planned batches: {r.ExecutionPlan?.Batches.Count ?? 0}; conflict edges: {r.ExecutionPlan?.Statistics.ConflictEdges ?? 0}.</p><h2>Execution counters</h2><p>Mutants: {c.TotalMutants} total, {c.CoveredMutants} covered, {c.UncoveredMutants} uncovered; tests {c.TotalTests}; baseline executions {c.BaselineTestExecutions}; mutation executions {c.MutationTestExecutions}; controller/test runner starts {c.TestRunnerProcessStarts}; testhost starts {c.TestHostProcessStarts}; discovery operations {c.TestDiscoveryOperations}; builds {c.BuildInvocations}; worker restarts {c.WorkerRestarts}; worker failures {c.WorkerFailures}; execution units {c.MutationBatches}, average mutants/unit {c.AverageMutantsPerBatch:F2}; worker busy {c.TotalWorkerBusyTimeMs} ms, idle {c.TotalWorkerIdleTimeMs} ms; observed process starts {r.ProcessStarts}.</p><table><thead><tr><th>Status</th><th>Source</th><th>Operator</th><th>Transformation</th><th>Tests</th></tr></thead><tbody>");
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
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true }; jsonOptions.Converters.Add(new JsonStringEnumConverter());
    if (selectedReports.Contains("json"))
    {
      await File.WriteAllTextAsync(result.JsonReportPath, JsonSerializer.Serialize(result, jsonOptions), ct);
    }

    if (selectedReports.Contains("html"))
    {
      await File.WriteAllTextAsync(result.HtmlReportPath, Html(result), ct);
    }

    var reportingMs = timer.ElapsedMilliseconds;
    result = result with { Timings = result.Timings with { ReportingMs = reportingMs }, TotalDurationMs = runTimer.ElapsedMilliseconds };
    if (File.Exists(result.JsonReportPath))
    {
      await File.WriteAllTextAsync(result.JsonReportPath, JsonSerializer.Serialize(result, jsonOptions), ct);
    }

    if (File.Exists(result.HtmlReportPath))
    {
      await File.WriteAllTextAsync(result.HtmlReportPath, Html(result), ct);
    }
    return result;
  }

}
