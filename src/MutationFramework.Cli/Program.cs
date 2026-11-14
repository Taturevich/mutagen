using System.Text.Json;
using MutationFramework.Sdk;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("mutate run --project <csproj> --tests <csproj[,csproj]> [--workers N] [--output DIR] [--report json,html] [--timeout seconds] [--filter text]\nmutate discover --project <csproj>\nmutate plan --project <csproj> --tests <csproj>\nmutate report <report.json>");
    return 0;
}

string command = args[0];
try
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) values[args[i][2..]] = args[++i];
        else if (command == "report" && i == 1) values["input"] = args[i];
    }
    if (command == "report")
    {
        var json = File.ReadAllText(values["input"]); using var doc = JsonDocument.Parse(json);
        Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true })); return 0;
    }
    if (command is not ("run" or "discover" or "plan")) throw new ArgumentException($"Unknown command '{command}'.");
    if (!values.TryGetValue("project", out var project)) throw new ArgumentException("--project is required.");
    if (command == "discover") { Console.WriteLine($"Project: {Path.GetFullPath(project)}"); Console.WriteLine("Discovery runs as part of mutate run; use --verbose for diagnostics."); return 0; }
    if (!values.TryGetValue("tests", out var tests)) throw new ArgumentException("--tests is required.");
    var options = new MutationOptions
    {
        ProjectPath = project,
        TestProjects = tests.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        MaxWorkers = values.TryGetValue("workers", out var workers) ? int.Parse(workers) : Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        OutputDirectory = values.GetValueOrDefault("output", "mutation-results"),
        Report = values.GetValueOrDefault("report", "json,html"),
        TimeoutSeconds = values.TryGetValue("timeout", out var timeout) ? int.Parse(timeout) : 120,
        TestFilter = values.GetValueOrDefault("filter"), Include = values.GetValueOrDefault("include"), Exclude = values.GetValueOrDefault("exclude")
    };
    var result = await MutationEngine.RunAsync(options);
    if (command == "plan") Console.WriteLine($"Planned {result.Mutants.Count} mutants; covered {result.Mutants.Count(x => x.Status != MutationStatus.NoCoverage)}; uncovered {result.NoCoverageCount}.");
    return result.OverallStatus == "Completed" ? 0 : 5;
}
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
catch (BaselineFailureException ex) { Console.Error.WriteLine(ex.Message); foreach (var e in ex.Errors) Console.Error.WriteLine(e); return 3; }
catch (OperationCanceledException) { Console.Error.WriteLine("Execution canceled."); return 5; }
catch (Exception ex) { Console.Error.WriteLine(ex); return 4; }
