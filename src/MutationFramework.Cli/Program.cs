using System.Text.Json;

using MutationFramework.Sdk;

if (Environment.GetEnvironmentVariable(TestControllerLease.TargetVariable) is { Length: > 0 } controllerTarget)
{
  return await TestControllerLease.RunProxyAsync(controllerTarget, args);
}

static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseResourceLocks(string? value)
{
  if (string.IsNullOrWhiteSpace(value))
  {
    return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
  }

  var parsed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
  foreach (var mapping in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
  {
    var separator = mapping.IndexOf("=>", StringComparison.Ordinal);
    if (separator <= 0 || separator + 2 >= mapping.Length)
    {
      throw new ArgumentException("--resource-locks entries must use testId=>lock[,lock];testId=>lock syntax.");
    }

    var testId = mapping[..separator].Trim();
    var locks = mapping[(separator + 2)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (locks.Length == 0 || !parsed.TryAdd(testId, locks))
    {
      throw new ArgumentException($"--resource-locks contains an empty or duplicate test identity: {testId}");
    }
  }
  return parsed;
}

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
  Console.WriteLine("Execution progress: --progress-interval seconds (default 10; 0 disables). Periodic counts, active workers, throughput and approximate ETA are written to stderr.");
  Console.WriteLine("mutate run --project <csproj> [--projects <csproj[,csproj]>] --tests <csproj[,csproj]> [--baseline FILE --baseline-scope KEY] [--save-baseline FILE --baseline-scope KEY] [--framework tfm] [--workers N] [--execution-mode auto|isolated|batched] [--test-host-isolation per-test|per-mutant|per-batch|per-resource-group] [--mutators id[,id]] [--resource-locks testId=>lock[,lock];testId=>lock] [--cache DIR] [--mutation-result-cache-scope KEY] [--output DIR] [--report json,html] [--timeout seconds] [--filter text]\nmutate discover --project <csproj> --tests <csproj>\nmutate plan --project <csproj> --tests <csproj> [--execution-mode auto|isolated|batched] [--output DIR]\nmutate report <report.json>");
  Console.WriteLine("Recovery: --checkpoint DIR --checkpoint-scope KEY (unchanged external-state attestation; fresh baseline is always validated).\nDeadlines: --timeout seconds (per case), --host-startup-timeout seconds (default 60), --run-timeout seconds (shared run cap; default 1800).\nAuto execution remains isolated; mixed-mutant hosts require explicit same-process equivalence.");
  return 0;
}

using var runCancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, cancellation) =>
{
  cancellation.Cancel = !runCancellation.IsCancellationRequested;
  runCancellation.Cancel();
};
var command = args[0];
try
{
  var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
  for (var i = 1; i < args.Length; i++)
  {
    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
    {
      values[args[i][2..]] = args[++i];
    }
    else if (command == "report" && i == 1)
    {
      values["input"] = args[i];
    }
  }
  if (command == "report")
  {
    var json = File.ReadAllText(values["input"]);
    using var doc = JsonDocument.Parse(json);
    Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
  }
  if (command is not ("run" or "discover" or "plan"))
  {
    throw new ArgumentException($"Unknown command '{command}'.");
  }

  if (!values.TryGetValue("project", out var project))
  {
    throw new ArgumentException("--project is required.");
  }

  if (!values.TryGetValue("tests", out var tests))
  {
    throw new ArgumentException("--tests is required.");
  }

  var testHostIsolation = values.GetValueOrDefault("test-host-isolation", "per-mutant").ToLowerInvariant() switch
  {
    "per-test" => TestHostIsolationPolicy.PerTest,
    "per-mutant" => TestHostIsolationPolicy.PerMutant,
    "per-batch" => TestHostIsolationPolicy.PerBatch,
    "per-resource-group" => TestHostIsolationPolicy.PerResourceGroup,
    _ => throw new ArgumentException("--test-host-isolation must be per-test, per-mutant, per-batch, or per-resource-group.")
  };
  var options = new MutationOptions
  {
    ProjectPath = project,
    ProductionProjects = values.TryGetValue("projects", out var productionProjects)
        ? productionProjects.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        : [],
    TestProjects = tests.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
    MaxWorkers = values.TryGetValue("workers", out var workers) ? int.Parse(workers) : Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
    OutputDirectory = values.GetValueOrDefault("output", "mutation-results"),
    Report = values.GetValueOrDefault("report", "json,html"),
    TimeoutSeconds = values.TryGetValue("timeout", out var timeout) ? int.Parse(timeout) : 120,
    MaxRunTimeoutSeconds = values.TryGetValue("run-timeout", out var runTimeout) ? int.Parse(runTimeout) : 1800,
    HostStartupTimeoutSeconds = values.TryGetValue("host-startup-timeout", out var startupTimeout) ? int.Parse(startupTimeout) : 60,
    ProgressIntervalSeconds = values.TryGetValue("progress-interval", out var progressInterval) ? int.Parse(progressInterval) : 10,
    CheckpointDirectory = values.GetValueOrDefault("checkpoint"),
    CheckpointScopeKey = values.GetValueOrDefault("checkpoint-scope"),
    BaselinePath = values.GetValueOrDefault("baseline"),
    SaveBaselinePath = values.GetValueOrDefault("save-baseline"),
    BaselineScopeKey = values.GetValueOrDefault("baseline-scope"),
    ExecutionMode = values.TryGetValue("execution-mode", out var executionMode) && Enum.TryParse<MutationExecutionMode>(executionMode, true, out var parsedMode) ? parsedMode : MutationExecutionMode.Auto,
    TestHostIsolationPolicy = testHostIsolation,
    TestFilter = values.GetValueOrDefault("filter"),
    Include = values.GetValueOrDefault("include"),
    Exclude = values.GetValueOrDefault("exclude"),
    Mutators = values.TryGetValue("mutators", out var mutators) ? mutators.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) : null,
    TargetFramework = values.GetValueOrDefault("framework"),
    CacheDirectory = values.GetValueOrDefault("cache"),
    MutationResultCacheScopeKey = values.GetValueOrDefault("mutation-result-cache-scope"),
    ResourceLocksByTestId = ParseResourceLocks(values.GetValueOrDefault("resource-locks"))
  };
  if (values.ContainsKey("execution-mode") && !Enum.TryParse<MutationExecutionMode>(values["execution-mode"], true, out _))
  {
    throw new ArgumentException("--execution-mode must be auto, isolated, or batched.");
  }

  if (command == "discover")
  {
    var result = await MutationEngine.DiscoverAsync(options, runCancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
  }
  var run = command == "plan" ? await MutationEngine.PlanAsync(options, runCancellation.Token) : await MutationEngine.RunAsync(options, runCancellation.Token);
  if (command == "plan")
  {
    Console.WriteLine($"Planned {run.Mutants.Count} mutants; covered {run.Counters.CoveredMutants}; uncovered {run.NoCoverageCount}. Plan report: {run.JsonReportPath}");
  }

  return run.OverallStatus is "Completed" or "Planned" ? 0 : 5;
}
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
catch (BaselineFailureException ex) { Console.Error.WriteLine(ex.Message); foreach (var e in ex.Errors) { Console.Error.WriteLine(e); } return 3; }
catch (OperationCanceledException) { Console.Error.WriteLine("Execution canceled."); return 5; }
catch (Exception ex) { Console.Error.WriteLine(ex); return 4; }
