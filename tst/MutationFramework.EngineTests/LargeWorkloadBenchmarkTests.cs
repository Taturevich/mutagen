using static MutationFramework.EngineTests.TestWorkspace;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MutationFramework.Sdk;
using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class LargeWorkloadBenchmarkTests
{
  [TestCase("(value()) >= 10", "value() >= 10", true)]
  [TestCase("(first()) || (second())", "first() || second()", true)]
  [TestCase("(amount > 100) || preferred", "amount > 100 || preferred", true)]
  [TestCase("a - (b - c)", "a - b - c", false)]
  [TestCase("(a + b) * c", "a + b * c", false)]
  [TestCase("\"a b\"", "\"ab\"", false)]
  [Category("Unit")]
  public void ReplacementNormalizationRemovesOnlyStructurallyRedundantParentheses(
      string left, string right, bool equivalent)
  {
    Assert.That(string.Equals(NormalizeReplacement(left), NormalizeReplacement(right), StringComparison.Ordinal),
        Is.EqualTo(equivalent));
  }

  [TestCase("ResourceGroupScheduling: split 20 batches into 80 process-isolated resource-group work units across 4 covered groups, bounded by 4 workers.", 5, 20, 4)]
  [TestCase("ResourceConstrainedConcurrency: reduced mutation workers from 5 to 2 because every batch shares a resource lock.", 5, 20, 2)]
  [TestCase("", 5, 20, 5)]
  [Category("Unit")]
  public void EffectiveWorkerCountUsesTheSchedulerDiagnostic(string diagnostic, int requestedWorkers, int batches, int expected)
  {
    Assert.That(ResolveEffectiveWorkerCount([diagnostic], requestedWorkers, batches), Is.EqualTo(expected));
  }

  [Test, Explicit("Compares the current matched small-workload report with the pinned Stryker report.")]
  [Category("Performance")]
  public void SmallWorkloadMatchesPinnedStrykerExecutableMutants()
  {
    var root = FindRepositoryRoot();
    var strykerReport = Path.Combine(root, "benchmarks", "results", "stryker-small-matched-current", "reports", "mutation-report.json");
    var mutagenReport = Path.Combine(root, "benchmarks", "results", "mutagen-small-matched-no-removal", "mutation-report.json");
    using var report = JsonDocument.Parse(File.ReadAllText(mutagenReport));
    var differential = CompareWithStryker(strykerReport, report.RootElement.GetProperty("Mutants"), includeIgnored: false);

    Assert.That(differential.StrykerMutants, Is.EqualTo(28));
    Assert.That(differential.MutagenMutants, Is.EqualTo(28));
    Assert.That(differential.Common, Is.EqualTo(28));
    Assert.That(differential.MutagenOnly, Is.Zero);
    Assert.That(differential.StrykerOnly, Is.Zero);
    Assert.That(differential.OutcomeDifferences, Is.Zero);
    Assert.That(differential.CoverageAssociationDifferences, Is.Zero);
  }

  [Test, Explicit("Runs three matched 2,000-mutant SQLite workloads and records machine-specific measurements.")]
  [Category("Performance")]
  public async Task LargeSqliteWorkloadIsMeasuredAcrossRepeatedRuns()
  {
    const int runCount = 3;
    var repositoryRoot = FindRepositoryRoot();
    var workers = int.TryParse(Environment.GetEnvironmentVariable("MUTAGEN_LARGE_WORKERS"), out var configuredWorkers)
        ? Math.Clamp(configuredWorkers, 1, 32)
        : 4;
    var isolationPolicy = Environment.GetEnvironmentVariable("MUTAGEN_LARGE_ISOLATION")?.Trim().ToLowerInvariant() switch
    {
      null or "" or "per-resource-group" => TestHostIsolationPolicy.PerResourceGroup,
      "per-batch" => TestHostIsolationPolicy.PerBatch,
      "per-mutant" => TestHostIsolationPolicy.PerMutant,
      var configured => throw new ArgumentException($"Unsupported MUTAGEN_LARGE_ISOLATION value '{configured}'. Use per-mutant, per-batch, or per-resource-group.")
    };
    var isolationArgument = isolationPolicy switch
    {
      TestHostIsolationPolicy.PerBatch => "per-batch",
      TestHostIsolationPolicy.PerMutant => "per-mutant",
      _ => "per-resource-group"
    };
    var benchmarkName = isolationPolicy switch
    {
      TestHostIsolationPolicy.PerBatch => $"mutagen-large-perbatch-current-workers-{workers}",
      TestHostIsolationPolicy.PerMutant => $"mutagen-large-permutant-current-workers-{workers}",
      _ => $"mutagen-large-workers-{workers}"
    };
    var benchmarkRoot = Path.Combine(repositoryRoot, "benchmarks", "results", benchmarkName);
    var sqliteRoot = Path.Combine(benchmarkRoot, "sqlite");
    var cacheDirectory = Path.Combine(benchmarkRoot, "cache");
    Directory.CreateDirectory(sqliteRoot);
    var previousSqliteRoot = Environment.GetEnvironmentVariable("MUTAGEN_LARGE_SQLITE_ROOT");
    Environment.SetEnvironmentVariable("MUTAGEN_LARGE_SQLITE_ROOT", sqliteRoot);

    var productionProject = Path.Combine(repositoryRoot, "benchmarks", "LargeIntegrationSample", "LargeIntegrationSample", "LargeIntegrationSample.csproj");
    var testProject = Path.Combine(repositoryRoot, "benchmarks", "LargeIntegrationSample", "LargeIntegrationSample.Tests", "LargeIntegrationSample.Tests.csproj");
    var baseOptions = new MutationOptions
    {
      ProjectPath = productionProject,
      TestProjects = [testProject],
      Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
      MaxWorkers = workers,
      TimeoutSeconds = 120,
      ExecutionMode = MutationExecutionMode.Batched,
      TestHostIsolationPolicy = isolationPolicy,
      CacheDirectory = cacheDirectory
    };

    try
    {
      var discovery = await MutationEngine.DiscoverAsync(baseOptions);
      Assert.That(discovery.Tests, Has.Length.EqualTo(100));
      var resourceLocks = BuildShardLocks(discovery.Tests);
      var measurements = new List<RunMeasurement>(runCount);
      string? expectedOutcomeDigest = null;
      for (var runIndex = 0; runIndex < runCount; runIndex++)
      {
        var processSample = new ProcessSample(SnapshotTrackedProcesses());
        using var sampleCancellation = new CancellationTokenSource();
        var sampler = SampleProcessesAsync(processSample, sampleCancellation.Token);
        var timer = Stopwatch.StartNew();
        var outputPath = Path.Combine(benchmarkRoot, $"run-{runIndex + 1}-{Guid.NewGuid():N}");
        var cliAssembly = Path.Combine(repositoryRoot, "src", "MutationFramework.Cli", "bin", "Debug", "net11.0", "MutationFramework.Cli.dll");
        Assert.That(File.Exists(cliAssembly), Is.True, $"Build the CLI before running this benchmark: {cliAssembly}");
        var startInfo = new ProcessStartInfo("dotnet")
        {
          WorkingDirectory = repositoryRoot,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliAssembly);
        startInfo.ArgumentList.Add("run");
        AddArgument(startInfo, "--project", productionProject);
        AddArgument(startInfo, "--tests", testProject);
        AddArgument(startInfo, "--framework", "net11.0");
        AddArgument(startInfo, "--workers", workers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--timeout", "120");
        AddArgument(startInfo, "--execution-mode", "batched");
        AddArgument(startInfo, "--test-host-isolation", isolationArgument);
        AddArgument(startInfo, "--mutators", "binary-operators");
        AddArgument(startInfo, "--cache", cacheDirectory);
        AddArgument(startInfo, "--output", outputPath);
        AddArgument(startInfo, "--report", "json");
        AddArgument(startInfo, "--resource-locks", SerializeResourceLocks(resourceLocks));
        try
        {
          using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the Mutagen CLI benchmark process.");
          var stdoutTask = process.StandardOutput.ReadToEndAsync();
          var stderrTask = process.StandardError.ReadToEndAsync();
          await process.WaitForExitAsync();
          var stdout = await stdoutTask;
          var stderr = await stderrTask;
          if (process.ExitCode != 0)
          {
            throw new InvalidOperationException($"Mutagen CLI benchmark exited with code {process.ExitCode}.\n{stdout}\n{stderr}");
          }
        }
        finally
        {
          sampleCancellation.Cancel();
          await sampler;
        }
        timer.Stop();

        var mutationReportPath = Path.Combine(outputPath, "mutation-report.json");
        using var reportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(mutationReportPath));
        var reportRoot = reportDocument.RootElement;
        var outcomes = reportRoot.GetProperty("Mutants").EnumerateArray().Select(mutant => new MutationIdentityOutcome(
            mutant.GetProperty("Id").GetString()!,
            Path.GetFileName(mutant.GetProperty("SourceFile").GetString()!),
            mutant.GetProperty("Line").GetInt32(),
            mutant.GetProperty("Column").GetInt32(),
            mutant.GetProperty("Replacement").GetString()!,
            mutant.GetProperty("Status").GetString()!,
            mutant.GetProperty("CoveringTests").EnumerateArray().Select(test => test.GetString()!).ToArray())).ToArray();
        var outcomeDigest = ComputeOutcomeDigest(outcomes);
        expectedOutcomeDigest ??= outcomeDigest;
        var counters = JsonSerializer.Deserialize<ExecutionCounters>(reportRoot.GetProperty("Counters").GetRawText())!;
        var timings = JsonSerializer.Deserialize<PhaseTimings>(reportRoot.GetProperty("Timings").GetRawText())!;
        var killed = reportRoot.GetProperty("KilledCount").GetInt32();
        var survived = reportRoot.GetProperty("SurvivedCount").GetInt32();
        var noCoverage = reportRoot.GetProperty("NoCoverageCount").GetInt32();
        var totalDurationMs = reportRoot.GetProperty("TotalDurationMs").GetInt64();
        var diagnostics = reportRoot.GetProperty("Diagnostics").EnumerateArray().Select(message => message.GetString()!).ToArray();
        var effectiveWorkers = ResolveEffectiveWorkerCount(diagnostics, workers, counters.MutationBatches);
        var resourceGroupDiagnostic = diagnostics.SingleOrDefault(message => message.StartsWith("ResourceGroupScheduling:", StringComparison.Ordinal));
        var scheduledWorkUnits = resourceGroupDiagnostic is null ? counters.MutationBatches :
            int.Parse(Regex.Match(resourceGroupDiagnostic, @"into (\d+) process-isolated").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
        var resourceGroupCount = isolationPolicy == TestHostIsolationPolicy.PerResourceGroup
            ? int.Parse(Regex.Match(diagnostics.Single(message => message.StartsWith("BaselineCoverageCollector:", StringComparison.Ordinal)),
                @"across (\d+) independently hosted").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        Assert.That(reportRoot.GetProperty("OverallStatus").GetString(), Is.EqualTo("Completed"), string.Join(Environment.NewLine, diagnostics));
        Assert.That(outcomes, Has.Length.EqualTo(2_000));
        Assert.That(killed, Is.EqualTo(2_000));
        Assert.That(counters.MutationTestExecutions, Is.GreaterThanOrEqualTo(2_000));
        Assert.That(counters.BaselineTestExecutions, Is.EqualTo(100));
        if (isolationPolicy == TestHostIsolationPolicy.PerResourceGroup)
        {
          Assert.That(counters.TestHostProcessStarts, Is.EqualTo(scheduledWorkUnits + resourceGroupCount + 1));
          Assert.That(effectiveWorkers, Is.EqualTo(Math.Min(workers, resourceGroupCount)));
        }
        else
        {
          Assert.That(counters.TestHostProcessStarts, Is.GreaterThanOrEqualTo(scheduledWorkUnits));
          Assert.That(effectiveWorkers, Is.LessThanOrEqualTo(workers));
        }
        Assert.That(outcomeDigest, Is.EqualTo(expectedOutcomeDigest), $"Mutation outcomes changed on run {runIndex + 1}.");

        measurements.Add(new RunMeasurement(
            runIndex + 1,
            timer.ElapsedMilliseconds,
            totalDurationMs,
            timings,
            counters,
            effectiveWorkers,
            killed,
            survived,
            noCoverage,
            outcomes.Length,
            diagnostics.Count(message => message.StartsWith("CacheHit:", StringComparison.Ordinal)),
            processSample.PeakAggregateWorkingSetBytes,
            processSample.ObservedProcessIds.Count,
            outcomeDigest,
            mutationReportPath));
        TestContext.Progress.WriteLine(JsonSerializer.Serialize(measurements[^1], JsonOptions));
      }

      var strykerReportPath = Path.Combine(repositoryRoot, "benchmarks", "results", "stryker-large-sqlite-5.0.0", "reports", "mutation-report.json");
      using var finalReportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(measurements[^1].ReportPath));
      var differential = CompareWithStryker(strykerReportPath, finalReportDocument.RootElement.GetProperty("Mutants"));
      Assert.That(differential.MutagenOnly, Is.Zero);
      Assert.That(differential.StrykerOnly, Is.Zero);
      Assert.That(differential.OutcomeDifferences, Is.Zero);
      Assert.That(differential.CoverageAssociationDifferences, Is.Zero);

      var report = new BenchmarkMeasurement(
          2,
          DateTimeOffset.UtcNow,
          new WorkloadDescription("LargeIntegrationSample", 2_000, 100, workers, $"batched/{isolationArgument}"),
          measurements,
          expectedOutcomeDigest!,
          differential);
      var artifactName = isolationPolicy switch
      {
        TestHostIsolationPolicy.PerBatch => $"mutagen-large-workers-{workers}-per-batch.json",
        TestHostIsolationPolicy.PerMutant => $"mutagen-large-workers-{workers}-per-mutant.json",
        _ => $"mutagen-large-workers-{workers}.json"
      };
      var artifactPath = Path.Combine(repositoryRoot, "benchmarks", "results", artifactName);
      File.WriteAllText(artifactPath, JsonSerializer.Serialize(report, JsonOptions));
      TestContext.Progress.WriteLine($"Benchmark artifact: {artifactPath}");
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTAGEN_LARGE_SQLITE_ROOT", previousSqliteRoot);
    }
  }

  private static Dictionary<string, IReadOnlyList<string>> BuildShardLocks(IReadOnlyList<TestIdentity> tests) =>
      tests.ToDictionary(test => test.Id, test =>
      {
        var opening = test.DisplayName.LastIndexOf('(');
        var closing = test.DisplayName.LastIndexOf(')');
        Assert.That(opening, Is.GreaterThan(0), $"Unexpected SQLite test display name: {test.DisplayName}");
        Assert.That(closing, Is.GreaterThan(opening), $"Unexpected SQLite test display name: {test.DisplayName}");
        var group = int.Parse(test.DisplayName.AsSpan(opening + 1, closing - opening - 1), System.Globalization.CultureInfo.InvariantCulture);
        return (IReadOnlyList<string>)[$"sqlite-shard-{group % 4}"];
      }, StringComparer.Ordinal);

  private static int ResolveEffectiveWorkerCount(IReadOnlyList<string> diagnostics, int requestedWorkers, int mutationBatches)
  {
    var resourceGroupDiagnostic = diagnostics.SingleOrDefault(message =>
        message.StartsWith("ResourceGroupScheduling:", StringComparison.Ordinal));
    if (resourceGroupDiagnostic is not null)
    {
      return ParsePositiveWorkerCount(resourceGroupDiagnostic, @"bounded by (\d+) workers");
    }

    var concurrencyDiagnostic = diagnostics.SingleOrDefault(message =>
        message.StartsWith("ResourceConstrainedConcurrency:", StringComparison.Ordinal));
    return concurrencyDiagnostic is null
        ? Math.Min(requestedWorkers, mutationBatches)
        : ParsePositiveWorkerCount(concurrencyDiagnostic, @"\bto (\d+)");
  }

  private static int ParsePositiveWorkerCount(string diagnostic, string pattern)
  {
    var match = Regex.Match(diagnostic, pattern);
    if (!match.Success || !int.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var count) || count < 1)
    {
      throw new InvalidOperationException($"Unable to parse effective worker count from diagnostic: {diagnostic}");
    }

    return count;
  }

  private static string SerializeResourceLocks(IReadOnlyDictionary<string, IReadOnlyList<string>> resourceLocks) =>
      string.Join(';', resourceLocks.OrderBy(pair => pair.Key, StringComparer.Ordinal)
          .Select(pair => $"{pair.Key}=>{string.Join(',', pair.Value.Order(StringComparer.Ordinal))}"));

  private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
  {
    startInfo.ArgumentList.Add(name);
    startInfo.ArgumentList.Add(value);
  }

  private static HashSet<int> SnapshotTrackedProcesses()
  {
    var processIds = new HashSet<int>();
    foreach (var processName in new[] { "dotnet", "testhost", "vstest.console" })
    {
      foreach (var process in Process.GetProcessesByName(processName))
      {
        using (process)
        {
          try { processIds.Add(process.Id); }
          catch (InvalidOperationException) { }
        }
      }
    }
    return processIds;
  }

  private static async Task SampleProcessesAsync(ProcessSample sample, CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      long aggregateWorkingSet = 0;
      foreach (var processName in new[] { "dotnet", "testhost", "vstest.console" })
      {
        foreach (var process in Process.GetProcessesByName(processName))
        {
          using (process)
          {
            try
            {
              if (!process.HasExited && !sample.PreexistingProcessIds.Contains(process.Id))
              {
                sample.ObservedProcessIds.TryAdd(process.Id, 0);
                aggregateWorkingSet += process.WorkingSet64;
              }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
          }
        }
      }
      sample.Record(aggregateWorkingSet);
      try
      {
        await Task.Delay(500, cancellationToken);
      }
      catch (OperationCanceledException)
      {
        break;
      }
    }
  }

  private static string ComputeOutcomeDigest(IEnumerable<MutationIdentityOutcome> mutants)
  {
    var canonical = string.Join('\n', mutants.OrderBy(mutant => mutant.Id, StringComparer.Ordinal)
        .Select(mutant => $"{mutant.Id}|{mutant.Status}|{string.Join(',', mutant.CoveringTests.Order(StringComparer.Ordinal))}"));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
  }

  private static StrykerDifferential CompareWithStryker(string reportPath, JsonElement mutagenMutantsElement,
      bool includeIgnored = true)
  {
    using var strykerDocument = JsonDocument.Parse(File.ReadAllText(reportPath));
    var strykerRoot = strykerDocument.RootElement;
    var testNames = strykerRoot.GetProperty("testFiles").EnumerateObject()
        .SelectMany(file => file.Value.GetProperty("tests").EnumerateArray())
        .ToDictionary(test => test.GetProperty("id").GetString()!, test => test.GetProperty("name").GetString()!, StringComparer.Ordinal);
    var strykerMutants = strykerRoot.GetProperty("files").EnumerateObject()
        .SelectMany(file => file.Value.GetProperty("mutants").EnumerateArray()
        .Where(mutant => includeIgnored || !string.Equals(mutant.GetProperty("status").GetString(), "Ignored", StringComparison.OrdinalIgnoreCase))
        .Select(mutant =>
        {
          var start = mutant.GetProperty("location").GetProperty("start");
          var coveredBy = mutant.GetProperty("coveredBy").EnumerateArray().Select(id => testNames[id.GetString()!]).ToArray();
          return new StrykerMutationIdentity(
              Path.GetFileName(file.Name),
              start.GetProperty("line").GetInt32(),
              start.GetProperty("column").GetInt32(),
              NormalizeReplacement(mutant.GetProperty("replacement").GetString()!),
              mutant.GetProperty("status").GetString()!,
              coveredBy);
        })).ToArray();
    var mutagenMutants = mutagenMutantsElement.EnumerateArray().Select(mutant => new MutationIdentityOutcome(
        mutant.GetProperty("Id").GetString()!,
        Path.GetFileName(mutant.GetProperty("SourceFile").GetString()!),
        mutant.GetProperty("Line").GetInt32(),
        mutant.GetProperty("Column").GetInt32(),
        mutant.GetProperty("Replacement").GetString()!,
        mutant.GetProperty("Status").GetString()!,
        mutant.GetProperty("CoveringTests").EnumerateArray().Select(test => test.GetString()!).ToArray())).ToArray();
    var mutagenByKey = mutagenMutants.ToDictionary(MutagenKey, StringComparer.Ordinal);
    var strykerByKey = strykerMutants.ToDictionary(StrykerKey, StringComparer.Ordinal);
    var commonKeys = mutagenByKey.Keys.Intersect(strykerByKey.Keys, StringComparer.Ordinal).ToArray();
    var outcomeDifferences = commonKeys.Count(key => !string.Equals(mutagenByKey[key].Status, strykerByKey[key].Status, StringComparison.OrdinalIgnoreCase));
    var coverageDifferences = commonKeys.Count(key =>
    {
      var mutagenTests = mutagenByKey[key].CoveringTests;
      var strykerTests = strykerByKey[key].CoveredBy;
      return mutagenTests.Length != strykerTests.Length || mutagenTests.Any(mutagenTest =>
          !strykerTests.Any(strykerTest => strykerTest.EndsWith(mutagenTest, StringComparison.Ordinal)));
    });
    return new StrykerDifferential(
        "5.0.0",
        mutagenMutants.Length,
        strykerMutants.Length,
        commonKeys.Length,
        mutagenByKey.Keys.Except(strykerByKey.Keys, StringComparer.Ordinal).Count(),
        strykerByKey.Keys.Except(mutagenByKey.Keys, StringComparer.Ordinal).Count(),
        outcomeDifferences,
        coverageDifferences,
        Path.GetFullPath(reportPath));

    static string MutagenKey(MutationIdentityOutcome mutant) =>
        $"{mutant.SourceFile}|{mutant.Line}|{mutant.Column}|{NormalizeReplacement(mutant.Replacement)}";
    static string StrykerKey(StrykerMutationIdentity mutant) =>
        $"{mutant.SourceFile}|{mutant.Line}|{mutant.Column}|{mutant.Replacement}";
  }

  private static string NormalizeReplacement(string replacement)
  {
    var expression = SyntaxFactory.ParseExpression(replacement);
    if (expression.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
    {
      return string.Join(",", SyntaxFactory.ParseTokens(replacement)
          .Where(token => !token.IsKind(SyntaxKind.EndOfFileToken))
          .Select(token => token.RawKind + ":" + Convert.ToHexString(Encoding.UTF8.GetBytes(
              token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.CharacterLiteralToken)
                  ? token.ValueText
                  : token.Text))));
    }

    var shape = ExpressionShape(expression);
    while (true)
    {
      var removedParentheses = false;
      foreach (var parenthesized in expression.DescendantNodesAndSelf().OfType<ParenthesizedExpressionSyntax>()
                   .OrderByDescending(node => node.SpanStart))
      {
        var candidateText = expression.ReplaceNode(parenthesized,
            parenthesized.Expression.WithTriviaFrom(parenthesized)).ToString();
        var candidate = SyntaxFactory.ParseExpression(candidateText);
        if (candidate.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
            !string.Equals(shape, ExpressionShape(candidate), StringComparison.Ordinal))
        {
          continue;
        }

        expression = candidate;
        removedParentheses = true;
        break;
      }

      if (!removedParentheses)
      {
        return shape;
      }

      shape = ExpressionShape(expression);
    }

    static string ExpressionShape(SyntaxNode node)
    {
      if (node is ParenthesizedExpressionSyntax parenthesized)
      {
        return ExpressionShape(parenthesized.Expression);
      }

      return node.RawKind + "(" + string.Join(",", node.ChildNodesAndTokens().Select(child => child.IsNode
          ? ExpressionShape(child.AsNode()!)
          : child.AsToken().RawKind + ":" + Convert.ToHexString(Encoding.UTF8.GetBytes(
              child.AsToken().IsKind(SyntaxKind.StringLiteralToken) || child.AsToken().IsKind(SyntaxKind.CharacterLiteralToken)
                  ? child.AsToken().ValueText
                  : child.AsToken().Text)))) + ")";
    }
  }



  private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

  private sealed class ProcessSample
  {
    private long _peakAggregateWorkingSetBytes;

    public ProcessSample(HashSet<int> preexistingProcessIds) => PreexistingProcessIds = preexistingProcessIds;
    public HashSet<int> PreexistingProcessIds { get; }
    public ConcurrentDictionary<int, byte> ObservedProcessIds { get; } = new();
    public long PeakAggregateWorkingSetBytes => Interlocked.Read(ref _peakAggregateWorkingSetBytes);

    public void Record(long workingSetBytes)
    {
      var peak = Interlocked.Read(ref _peakAggregateWorkingSetBytes);
      while (workingSetBytes > peak)
      {
        var prior = Interlocked.CompareExchange(ref _peakAggregateWorkingSetBytes, workingSetBytes, peak);
        if (prior == peak) { break; }
        peak = prior;
      }
    }
  }

  private sealed record WorkloadDescription(string Name, int Mutants, int TestCases, int Workers, string IsolationPolicy);
  private sealed record MutationIdentityOutcome(string Id, string SourceFile, int Line, int Column,
      string Replacement, string Status, string[] CoveringTests);
  private sealed record StrykerMutationIdentity(string SourceFile, int Line, int Column,
      string Replacement, string Status, string[] CoveredBy);
  private sealed record RunMeasurement(int Run, long SampledWallClockMs, long ReportWallClockMs, PhaseTimings Timings,
      ExecutionCounters Counters, int EffectiveWorkers, int Killed, int Survived, int NoCoverage, int Mutants, int CacheHitDiagnostics,
      long PeakAggregateWorkingSetBytes, int DistinctObservedProcesses, string OutcomeDigest, string ReportPath);
  private sealed record BenchmarkMeasurement(int SchemaVersion, DateTimeOffset MeasuredAtUtc,
      WorkloadDescription Workload, IReadOnlyList<RunMeasurement> Runs, string OutcomeDigest,
      StrykerDifferential StrykerDifferential);
  private sealed record StrykerDifferential(string ReferenceVersion, int MutagenMutants, int StrykerMutants,
      int Common, int MutagenOnly, int StrykerOnly, int OutcomeDifferences, int CoverageAssociationDifferences,
      string StrykerReportPath);
}
