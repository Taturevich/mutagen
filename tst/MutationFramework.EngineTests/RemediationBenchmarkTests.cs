using System.Diagnostics;
using System.Text.Json;

using Microsoft.CodeAnalysis;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class RemediationBenchmarkTests
{
  [Test, Explicit("Records planner and real Roslyn rewrite measurements before/after remediation.")]
  [Category("Performance")]
  public void MeasurePlanningAndRewriting()
  {
    var measurements = new List<object>();
    var maximum = int.TryParse(Environment.GetEnvironmentVariable("MUTAGEN_BENCHMARK_MAX"), out var configured) ? configured : 10000;
    var operations = Environment.GetEnvironmentVariable("MUTAGEN_BENCHMARK_OPERATIONS") ?? "plan,rewrite";
    foreach (var size in new[] { 100, 1000, 10000 }.Where(size => size <= maximum))
    {
      foreach (var pattern in new[] { "dense", "disjoint", "mixed" }.Where(_ => operations.Contains("plan", StringComparison.Ordinal)))
      {
        var items = Enumerable.Range(0, size).Select(i => new MutationWorkItem($"M{i:D6}",
            pattern == "dense" ? ["shared"] : pattern == "disjoint" ? [$"T{i}"] : new[] { $"T{i % 20}", $"T{i % 71}" }.Distinct().ToArray(), 1)).ToArray();
        for (var run = 0; run < 3; run++)
        {
          var allocated = GC.GetTotalAllocatedBytes(true);
          var timer = Stopwatch.StartNew();
          var plan = MutationPlanner.Build(items, [], MutationExecutionMode.Batched, true, 30, 4);
          measurements.Add(new
          {
            operation = "plan",
            size,
            pattern,
            run,
            elapsedMs = timer.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            edges = plan.Statistics.ConflictEdges,
            batches = plan.Batches.Count,
            peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64
          });
        }
      }
      foreach (var pattern in new[] { "one-file", "many-files", "same-node", "nested" }.Where(_ => operations.Contains("rewrite", StringComparison.Ordinal)))
      {
        var directory = Path.Combine(Path.GetTempPath(), "MutagenRewriteBench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
          var fileCount = pattern == "many-files" ? 20 : 1;
          var methods = pattern is "same-node" or "nested" ? size / 2 : size;
          var sources = new Dictionary<string, string>();
          var mapping = new Dictionary<string, string>();
          for (var file = 0; file < fileCount; file++)
          {
            var source = Path.Combine(directory, $"Source{file}.cs");
            var declarations = Enumerable.Range(0, methods).Where(index => index % fileCount == file).Select(index => pattern switch
            {
              "same-node" => $"public static bool M{index}(int x) {{ return x > 1; }}",
              "nested" => $"public static int M{index}(int x) {{ return (x + 1) + 2; }}",
              _ => $"public static int M{index}(int x) {{ return x + 1; }}"
            });
            sources[source] = $"public static class Fixture{file} {{ " + string.Join(" ", declarations) + " }";
            File.WriteAllText(source, sources[source]);
            mapping[source] = Path.Combine(directory, $"Target{file}.cs");
          }
          var candidates = MutationDiscovery.DiscoverCandidates(sources.Keys.ToArray(), Path.Combine(directory, "Fixture.csproj"),
              new MutationOptions
              {
                ProjectPath = Path.Combine(directory, "Fixture.csproj"),
                TestProjects = [],
                Mutators = new HashSet<string>(["binary-operators"])
              }, [], CancellationToken.None);
          Assert.That(candidates.Count, Is.EqualTo(size));
          for (var run = 0; run < 3; run++)
          {
            foreach (var (source, target) in mapping)
            {
              File.WriteAllText(target, sources[source]);
            }
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            MutationSchemaBuilder.RewriteSources(mapping, candidates, directory, directory);
            var elapsedMs = timer.Elapsed.TotalMilliseconds;
            measurements.Add(new
            {
              operation = "rewrite",
              size,
              pattern,
              run,
              elapsedMs,
              allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
              parsedTrees = fileCount,
              digest = CacheIdentity.Hash(string.Join("\n", mapping.Values.Select(File.ReadAllText))),
              normalizedDigest = CacheIdentity.Hash(string.Join("\n", mapping.Values.Select(file => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().NormalizeWhitespace().ToFullString()))),
              peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64
            });
          }
        }
        finally { Directory.Delete(directory, true); }
      }
    }
    var destination = Path.Combine(TestWorkspace.Root, "benchmarks", "results",
        "remediation-" + (Environment.GetEnvironmentVariable("MUTAGEN_MEASUREMENT_LABEL") ?? "current") + ".json");
    File.WriteAllText(destination, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
    TestContext.Progress.WriteLine(destination);
  }
}
