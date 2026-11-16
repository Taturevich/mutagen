using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MutationFramework.Sdk;
using NUnit.Framework;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class CoverageInstrumentationBenchmarkTests
{
  [Test, Explicit("Measures steady-state mutation probe overhead; timings are machine-specific.")]
  public void CoverageInstrumentationOverheadIsMeasuredForMatchedCompiledWorkloads()
  {
    const int operationsPerSample = 5_000_000;
    const int samples = 7;
    var root = Path.Combine(Path.GetTempPath(), "MutagenCoverageOverhead", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var source = Path.Combine(root, "Workload.cs");
    var project = Path.Combine(root, "Workload.csproj");
    var instrumentedSource = Path.Combine(root, "instrumented", "Workload.cs");
    Directory.CreateDirectory(Path.GetDirectoryName(instrumentedSource)!);
    File.WriteAllText(project, "<Project />");
    File.WriteAllText(source, "public static class Workload { public static long Execute(int iterations) { long total = 0; for (int index = 0; index < iterations; index++) { if ((index & 1) == 0) total += index + 3; else total += index - 3; } return total; } }");

    var options = new MutationOptions
    {
      ProjectPath = project,
      TestProjects = [],
      Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal)
    };
    var candidates = Engine.DiscoverCandidates([source], project, options, [], CancellationToken.None);
    Assert.That(candidates, Is.Not.Empty);
    File.Copy(source, instrumentedSource);
    Engine.RewriteSources(new Dictionary<string, string> { [source] = instrumentedSource }, candidates, root, root);
    var runtime = Engine.RuntimeSource(candidates.Select(candidate => candidate.SiteRuntimeId)
        .Distinct(StringComparer.Ordinal).Count());
    var reportPath = Path.Combine(FindRepositoryRoot(), "benchmarks", "results", "coverage-overhead", "runtime-probe-microbenchmark.json");
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    var probePath = Path.Combine(Path.GetTempPath(), "MutagenCoverageOverhead_" + Guid.NewGuid().ToString("N") + ".jsonl");
    var oldProbePath = Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE");
    var oldActiveId = Environment.GetEnvironmentVariable("MUTATION_ACTIVE_ID");

    try
    {
      Environment.SetEnvironmentVariable("MUTATION_ACTIVE_ID", null);
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", null);
      var uninstrumented = CompileLoad("CoverageOverhead_Uninstrumented", File.ReadAllText(source), null);
      var mutationChecks = CompileLoad("CoverageOverhead_MutationChecks", File.ReadAllText(instrumentedSource), runtime);
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", probePath);
      var coverageProbes = CompileLoad("CoverageOverhead_CoverageProbes", File.ReadAllText(instrumentedSource), runtime);

      var uninstrumentedResult = Measure(uninstrumented, operationsPerSample, samples);
      var mutationChecksResult = Measure(mutationChecks, operationsPerSample, samples);
      var coverageProbesResult = Measure(coverageProbes, operationsPerSample, samples);
      Assert.That(mutationChecksResult.Checksum, Is.EqualTo(uninstrumentedResult.Checksum));
      Assert.That(coverageProbesResult.Checksum, Is.EqualTo(uninstrumentedResult.Checksum));
      Assert.That(ReadProbeEnabled(mutationChecks.Assembly), Is.False, "The mutation-check variant must not record coverage probes.");
      Assert.That(ReadProbeEnabled(coverageProbes.Assembly), Is.True, "The coverage-probe variant must enable probe recording.");
      var hitSites = ReadHitSites(coverageProbes.Assembly);
      Assert.That(hitSites, Is.EqualTo(candidates.Select(candidate => candidate.SiteRuntimeId)
          .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
          "Every instrumented mutation site in the workload must be observed by the measured loop.");

      var report = new
      {
        schemaVersion = 1,
        measuredAtUtc = DateTimeOffset.UtcNow,
        environment = new
        {
          framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
          os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
          processorCount = Environment.ProcessorCount,
          compilationOptimization = "Release",
          benchmarkHarness = "Roslyn-emitted steady-state method microbenchmark"
        },
        workload = new
        {
          method = "long Execute(int iterations)",
          operationsPerSample,
          samples,
          mutationCandidates = candidates.Count,
          distinctCoverageSites = candidates.Select(candidate => candidate.SiteRuntimeId).Distinct(StringComparer.Ordinal).Count(),
          identicalOriginalBehavior = true
        },
        results = new
        {
          uninstrumented = uninstrumentedResult,
          mutationChecksWithoutCoverage = mutationChecksResult,
          coverageProbesEnabled = coverageProbesResult,
          mutationCheckOverheadPercent = PercentOverhead(uninstrumentedResult.MedianMilliseconds, mutationChecksResult.MedianMilliseconds),
          enabledCoverageProbeOverheadPercent = PercentOverhead(uninstrumentedResult.MedianMilliseconds, coverageProbesResult.MedianMilliseconds),
          coverageProbeIncrementalOverheadPercent = PercentOverhead(mutationChecksResult.MedianMilliseconds, coverageProbesResult.MedianMilliseconds)
        },
        limitations = new[]
        {
          "This isolates steady-state instrumented expression execution in one compiled method; it is not an end-to-end test-suite or testhost benchmark.",
          "Probe serialization at process exit, build time, test discovery, and baseline test scheduling are excluded.",
          "Results are machine-specific and use one local run; use the existing multi-run workload benchmarks for end-to-end comparisons."
        }
      };
      File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
      TestContext.Progress.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
      TestContext.Progress.WriteLine($"Coverage instrumentation measurements written to {reportPath}");
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", oldProbePath);
      Environment.SetEnvironmentVariable("MUTATION_ACTIVE_ID", oldActiveId);
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }

    static CompiledVariant CompileLoad(string assemblyName, string sourceText, string? runtimeSource)
    {
      var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
      var syntaxTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(sourceText, parseOptions, "Workload.cs") };
      if (runtimeSource is not null)
      {
        syntaxTrees.Add(CSharpSyntaxTree.ParseText(runtimeSource, parseOptions, "MutationRuntime.g.cs"));
      }
      var referencePaths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      var references = referencePaths.Select(path => MetadataReference.CreateFromFile(path));
      var compilation = CSharpCompilation.Create(assemblyName, syntaxTrees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
      using var assemblyBytes = new MemoryStream();
      var emit = compilation.Emit(assemblyBytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine,
          emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      var assembly = Assembly.Load(assemblyBytes.ToArray());
      var execute = assembly.GetType("Workload", throwOnError: true)!.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static)!
          .CreateDelegate<Func<int, long>>();
      return new(execute, assembly);
    }

    static Measurement Measure(CompiledVariant variant, int iterations, int sampleCount)
    {
      for (var warmup = 0; warmup < 3; warmup++)
      {
        _ = variant.Execute(iterations / 10);
      }
      var elapsed = new double[sampleCount];
      var allocated = new long[sampleCount];
      var checksum = 0L;
      for (var sample = 0; sample < sampleCount; sample++)
      {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        checksum = variant.Execute(iterations);
        timer.Stop();
        elapsed[sample] = timer.Elapsed.TotalMilliseconds;
        allocated[sample] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
      }
      Array.Sort(elapsed);
      Array.Sort(allocated);
      return new(elapsed[sampleCount / 2], elapsed, allocated[sampleCount / 2], checksum);
    }

    static double PercentOverhead(double baselineMilliseconds, double measuredMilliseconds) =>
        baselineMilliseconds == 0 ? 0 : (measuredMilliseconds - baselineMilliseconds) * 100 / baselineMilliseconds;

    static bool ReadProbeEnabled(Assembly assembly) =>
        (bool)assembly.GetType("MutationRuntime", throwOnError: true)!
            .GetField("ProbeEnabled", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    static string[] ReadHitSites(Assembly assembly)
    {
      var runtime = assembly.GetType("MutationRuntime", throwOnError: true)!;
      var hits = (int[])runtime.GetField("Hits", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
      return hits.Select((count, index) => (count, index)).Where(hit => hit.count != 0)
          .Select(hit => hit.index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal).ToArray();
    }
  }

  [Test, Explicit("Measures sparse per-test coverage drains; timings are machine-specific.")]
  public void SparseCoverageDrainOverheadIsMeasuredForMatchedCompiledWorkloads()
  {
    const int siteCount = 512;
    const int operationsPerSample = 10_000;
    const int samples = 7;
    const string previousDrain = "if (Interlocked.Exchange(ref Hits[index], 0) != 0) { sites.Add(index); }";
    const string guardedDrain = "if (Volatile.Read(ref Hits[index]) != 0 && Interlocked.Exchange(ref Hits[index], 0) != 0) { sites.Add(index); }";
    var optimizedRuntime = Engine.RuntimeSource(siteCount);
    var previousRuntime = optimizedRuntime.Replace(guardedDrain, previousDrain, StringComparison.Ordinal);
    Assert.That(previousRuntime, Does.Contain(previousDrain));
    var source = "public static class Workload { public static long Execute(int iterations) { long total = 0; for (int i = 0; i < iterations; i++) { MutationRuntime.Hit(0); MutationRuntime.Hit(128); MutationRuntime.Hit(511); total += MutationRuntime.GetCoverageData().Length; } return total; } }";
    var oldPerTestCoverage = Environment.GetEnvironmentVariable("MUTATION_PER_TEST_COVERAGE");
    var oldProbePath = Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE");
    try
    {
      Environment.SetEnvironmentVariable("MUTATION_PER_TEST_COVERAGE", "1");
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", null);
      var previous = CompileVariant("CoverageDrain_Previous", source, previousRuntime);
      var optimized = CompileVariant("CoverageDrain_Guarded", source, optimizedRuntime);
      var previousResult = MeasureDrain(previous, operationsPerSample, samples);
      var optimizedResult = MeasureDrain(optimized, operationsPerSample, samples);
      Assert.That(previousResult.Checksum, Is.EqualTo((long)operationsPerSample * 3));
      Assert.That(optimizedResult.Checksum, Is.EqualTo(previousResult.Checksum));

      var report = new
      {
        schemaVersion = 1,
        measuredAtUtc = DateTimeOffset.UtcNow,
        environment = new
        {
          framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
          os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
          processorCount = Environment.ProcessorCount,
          compilationOptimization = "Release",
          benchmarkHarness = "Roslyn-emitted sparse per-test coverage drain"
        },
        workload = new { siteCount, operationsPerSample, samples, activeSitesPerDrain = 3, identicalCoverageChecksum = true },
        results = new
        {
          previousAtomicExchangeEverySite = previousResult,
          guardedExchangeForHitSites = optimizedResult,
          improvementPercent = PercentImprovement(previousResult.MedianMilliseconds, optimizedResult.MedianMilliseconds)
        },
        limitations = new[]
        {
          "This isolates coverage-map drains in one compiled method; it is not an end-to-end testhost benchmark.",
          "The sparse-hit ratio and site count model one workload and should be compared with integration-suite measurements.",
          "The benchmark is machine-specific and does not measure concurrent test execution."
        }
      };
      var reportPath = Path.Combine(FindRepositoryRoot(), "benchmarks", "results", "coverage-overhead", "coverage-drain-microbenchmark.json");
      Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
      File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
      TestContext.Progress.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTATION_PER_TEST_COVERAGE", oldPerTestCoverage);
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", oldProbePath);
    }

    static CompiledVariant CompileVariant(string name, string source, string runtime)
    {
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), "Workload.cs"),
        CSharpSyntaxTree.ParseText(runtime, new CSharpParseOptions(LanguageVersion.Preview), "MutationRuntime.g.cs")
      };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)
          .Select(path => MetadataReference.CreateFromFile(path));
      var compilation = CSharpCompilation.Create(name, trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine,
          emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      var assembly = Assembly.Load(bytes.ToArray());
      var execute = assembly.GetType("Workload", throwOnError: true)!.GetMethod("Execute")!
          .CreateDelegate<Func<int, long>>();
      return new(execute, assembly);
    }

    static Measurement MeasureDrain(CompiledVariant variant, int iterations, int sampleCount)
    {
      for (var warmup = 0; warmup < 3; warmup++)
      {
        _ = variant.Execute(iterations / 10);
      }
      var elapsed = new double[sampleCount];
      var allocated = new long[sampleCount];
      var checksum = 0L;
      for (var sample = 0; sample < sampleCount; sample++)
      {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        checksum = variant.Execute(iterations);
        timer.Stop();
        elapsed[sample] = timer.Elapsed.TotalMilliseconds;
        allocated[sample] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
      }
      Array.Sort(elapsed);
      Array.Sort(allocated);
      return new(elapsed[sampleCount / 2], elapsed, allocated[sampleCount / 2], checksum);
    }

    static double PercentImprovement(double baselineMilliseconds, double measuredMilliseconds) =>
        baselineMilliseconds == 0 ? 0 : (baselineMilliseconds - measuredMilliseconds) * 100 / baselineMilliseconds;
  }

  [Test, Explicit("Measures cached per-test coverage reader lookup; timings are machine-specific.")]
  public void CachedCoverageReadersAvoidRepeatedAssemblyReflection()
  {
    const int operationsPerSample = 2_000;
    const int samples = 7;
    const string uncachedRead = "public static int[] ReadCoverage() { var sites = new System.Collections.Generic.SortedSet<int>(); foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies()) { System.Type? runtimeType; try { runtimeType = assembly.GetType(\"MutationRuntime\", false, false); } catch (System.NotSupportedException) { continue; } var method = runtimeType?.GetMethod(\"GetCoverageData\", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); if (method?.Invoke(null, null) is int[] values) { sites.UnionWith(values); } } return System.Linq.Enumerable.ToArray(sites); }";
    var originalCoverageMode = Environment.GetEnvironmentVariable("MUTATION_PER_TEST_COVERAGE");
    var originalProbePath = Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE");
    try
    {
      Environment.SetEnvironmentVariable("MUTATION_PER_TEST_COVERAGE", "1");
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", null);
      var workload = "public static class Workload { public static long Execute(int iterations) { long total = 0; for (var index = 0; index < iterations; index++) { MutationRuntime.Hit(0); total += ReadCoverage().Length; } return total; } " + uncachedRead + " }";
      var cachedWorkload = "public static class Workload { private static readonly System.Func<int[]> CoverageReader = MutationRuntime.GetCoverageData; public static long Execute(int iterations) { long total = 0; for (var index = 0; index < iterations; index++) { MutationRuntime.Hit(0); var sites = new System.Collections.Generic.SortedSet<int>(); sites.UnionWith(CoverageReader()); total += System.Linq.Enumerable.ToArray(sites).Length; } return total; } }";
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)
          .Select(path => MetadataReference.CreateFromFile(path));
      var uncached = CompileReaderVariant("CoverageReaders_Uncached", workload, references);
      var cached = CompileReaderVariant("CoverageReaders_Cached", cachedWorkload, references);
      var uncachedResult = MeasureReader(uncached, operationsPerSample, samples);
      var cachedResult = MeasureReader(cached, operationsPerSample, samples);
      Assert.That(uncachedResult.Checksum, Is.EqualTo((long)operationsPerSample));
      Assert.That(cachedResult.Checksum, Is.EqualTo(uncachedResult.Checksum));

      var report = new
      {
        schemaVersion = 1,
        measuredAtUtc = DateTimeOffset.UtcNow,
        environment = new
        {
          framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
          os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
          processorCount = Environment.ProcessorCount,
          compilationOptimization = "Release",
          loadedAssemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length
        },
        workload = new { operationsPerSample, samples, identicalCoverageChecksum = true },
        results = new
        {
          repeatedAssemblyReflection = uncachedResult,
          cachedTypedReader = cachedResult,
          improvementPercent = uncachedResult.MedianMilliseconds == 0 ? 0 :
              (uncachedResult.MedianMilliseconds - cachedResult.MedianMilliseconds) * 100 / uncachedResult.MedianMilliseconds
        },
        limitations = new[]
        {
          "This measures the collector's assembly lookup and invocation in the engine test process, not a full VSTest run.",
          "The loaded assembly count and process environment are machine-specific; integration measurements remain necessary."
        }
      };
      var reportPath = Path.Combine(FindRepositoryRoot(), "benchmarks", "results", "coverage-overhead", "coverage-reader-cache-microbenchmark.json");
      Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
      File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
      TestContext.Progress.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTATION_PER_TEST_COVERAGE", originalCoverageMode);
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", originalProbePath);
    }

    static CompiledVariant CompileReaderVariant(string name, string workload, IEnumerable<MetadataReference> references)
    {
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(workload, new CSharpParseOptions(LanguageVersion.Preview), "Workload.cs"),
        CSharpSyntaxTree.ParseText(Engine.RuntimeSource(1), new CSharpParseOptions(LanguageVersion.Preview), "MutationRuntime.g.cs")
      };
      var compilation = CSharpCompilation.Create(name, trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine,
          emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      var assembly = Assembly.Load(bytes.ToArray());
      var execute = assembly.GetType("Workload", throwOnError: true)!.GetMethod("Execute")!
          .CreateDelegate<Func<int, long>>();
      return new(execute, assembly);
    }

    static Measurement MeasureReader(CompiledVariant variant, int iterations, int sampleCount)
    {
      for (var warmup = 0; warmup < 2; warmup++) { _ = variant.Execute(iterations / 10); }
      var elapsed = new double[sampleCount];
      var allocated = new long[sampleCount];
      var checksum = 0L;
      for (var sample = 0; sample < sampleCount; sample++)
      {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        checksum = variant.Execute(iterations);
        timer.Stop();
        elapsed[sample] = timer.Elapsed.TotalMilliseconds;
        allocated[sample] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
      }
      Array.Sort(elapsed);
      Array.Sort(allocated);
      return new(elapsed[sampleCount / 2], elapsed, allocated[sampleCount / 2], checksum);
    }
  }

  private sealed record CompiledVariant(Func<int, long> Execute, Assembly Assembly);
  private sealed record Measurement(double MedianMilliseconds, double[] SamplesMilliseconds,
      long MedianAllocatedBytesPerSample, long Checksum);

  private static string FindRepositoryRoot()
  {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
      if (File.Exists(Path.Combine(directory.FullName, "MutationFramework.slnx")))
      {
        return directory.FullName;
      }
    }
    throw new DirectoryNotFoundException("Unable to locate MutationFramework.slnx from the benchmark test output directory.");
  }
}
