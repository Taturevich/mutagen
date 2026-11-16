using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

public enum MutationStatus { Pending, Killed, Survived, NoCoverage, CompileError, Unsupported, Timeout, InfrastructureError, Inconclusive }
public enum CoverageStatus { CoverageSuccess, CoverageUnavailable, CoverageCorrupted, CoverageCollectionFailed }
public enum TestRunTimeoutPolicy { PerTest, AggregatePerMutant, AggregatePerBatch, Mixed }
/// <summary>Controls whether tests for one mutant may share a testhost process. PerMutant matches grouped test-run behavior.</summary>
public enum TestHostIsolationPolicy { PerTest, PerMutant, PerBatch, PerResourceGroup, Mixed }
public sealed record TestIdentity(string Id, string Name, string DisplayName, long DurationMs = 0, string ExecutorUri = "", string SourceAssembly = "", string? ParameterizedCaseIdentity = null);
public sealed record TestExecutionProfile(string TestId, long BaselineDurationMs, IReadOnlyList<string> CoveredMutationSites,
    string? FixtureAffinity, bool IsolationRequired, IReadOnlyList<string> ResourceLocks, int TimeoutSeconds,
    IReadOnlyList<MutationExecutionMode> EligibleExecutionModes)
{
  public TestHostIsolationPolicy TestHostIsolationPolicy { get; init; } = TestHostIsolationPolicy.PerMutant;
  public TestRunTimeoutPolicy TimeoutPolicy { get; init; } = TestRunTimeoutPolicy.PerTest;
  public bool TestsSerializedInSharedHost { get; init; }
  public string? ResourceIsolationGroup { get; init; }
}
public sealed record MutationResult(string Id, string SiteId, string Operator, string SourceFile, int Line, int Column, string Original, string Replacement, MutationStatus Status, IReadOnlyList<string> CoveringTests, IReadOnlyList<string> KillingTests, long DurationMs = 0, string? Diagnostic = null);
public sealed record PhaseTimings(long DiscoveryMs, long BuildMs, long BaselineMs, long CoverageMs, long MutationGenerationMs, long ExecutionMs, long ReportingMs)
{
  public long ProbeSerializationMs { get; init; }
  public long CoverageMappingMs { get; init; }
  public long ProjectEvaluationMs { get; init; }
  public long ProjectCopyMs { get; init; }
  public long InstrumentationMs { get; init; }
  public long MutationPlanningMs { get; init; }
  public long WorkerInitializationMs { get; init; }
}
public sealed record ExecutionCounters(int TotalMutants, int CoveredMutants, int UncoveredMutants, int TotalTests, int BaselineTestExecutions, int MutationTestExecutions, int TestRunnerProcessStarts, int TestDiscoveryOperations, int BuildInvocations, int WorkerRestarts, int WorkerFailures, int MutationBatches, double AverageMutantsPerBatch, long TotalWorkerBusyTimeMs, long TotalWorkerIdleTimeMs)
{
  public int TestHostProcessStarts { get; init; }
}
public sealed record MutationRunResult(string OverallStatus, IReadOnlyList<MutationResult> Mutants, int KilledCount, int SurvivedCount, int NoCoverageCount, double? MutationScore, PhaseTimings Timings, long TotalDurationMs, IReadOnlyDictionary<string, IReadOnlyList<string>> TestToSites, IReadOnlyList<string> Diagnostics, string JsonReportPath, string HtmlReportPath)
{
  public int ReportSchemaVersion { get; init; } = 4;
  public IReadOnlyList<MutationResult> UnsupportedMutations { get; init; } = [];
  public ExecutionCounters Counters { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
  public int ProcessStarts { get; init; }
  public IReadOnlyList<TestIdentity> Tests { get; init; } = [];
  public IReadOnlyList<TestExecutionProfile> ExecutionProfiles { get; init; } = [];
  public MutationExecutionPlan? ExecutionPlan { get; init; }
  public IReadOnlyDictionary<string, CoverageStatus> CoverageStatuses { get; init; } = new Dictionary<string, CoverageStatus>();
}
public sealed record MutationCandidateInfo(string Id, string SiteId, string Operator, string SourceFile, int Line, int Column, string Original, string Replacement);
public sealed record MutationDiscoveryResult(IReadOnlyList<MutationCandidateInfo> Candidates, IReadOnlyList<TestIdentity> Tests, IReadOnlyList<string> Diagnostics, long DurationMs, int ProcessStarts)
{
  public IReadOnlyList<MutationResult> UnsupportedMutations { get; init; } = [];
}
public sealed class MutationOptions
{
  public required string ProjectPath { get; init; }
  public IReadOnlyList<string> ProductionProjects { get; init; } = [];
  public required IReadOnlyList<string> TestProjects { get; init; }
  public int MaxWorkers { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
  public string OutputDirectory { get; init; } = "mutation-results";
  public string Report { get; init; } = "json,html";
  public int TimeoutSeconds { get; init; } = 120;
  public MutationExecutionMode ExecutionMode { get; init; } = MutationExecutionMode.Auto;
  /// <summary>PerMutant reuses one host for tests of one mutant; PerBatch mixes coverage-disjoint mutants in one testhost; PerResourceGroup runs disjoint declared resource groups in separate hosts. Reuse requires caller attestation of same-process equivalence within each reused host and independent process equivalence across resource groups.</summary>
  public TestHostIsolationPolicy TestHostIsolationPolicy { get; init; } = TestHostIsolationPolicy.PerMutant;
  public string? TestFilter { get; init; }
  public string? Include { get; init; }
  public string? Exclude { get; init; }
  public IReadOnlySet<string>? Mutators { get; init; }
  public string? TargetFramework { get; init; }
  public string? CacheDirectory { get; init; }
  /// <summary>
  /// Enables validated per-mutant result reuse when this stable scope key changes whenever external test state can affect outcomes.
  /// Leave null to always execute mutants. Result reuse is restricted to per-test isolated execution.
  /// </summary>
  public string? MutationResultCacheScopeKey { get; init; }
  public IReadOnlyDictionary<string, IReadOnlyList<string>> ResourceLocksByTestId { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
  /// <summary>Optional explicit mutator registry for SDK extensions.</summary>
  public MutatorRegistry? Registry { get; init; }
}

public static class MutationEngine
{
  public static async Task<MutationRunResult> RunAsync(MutationOptions options, CancellationToken cancellationToken = default) => await Engine.RunAsync(options, true, cancellationToken);
  public static async Task<MutationRunResult> PlanAsync(MutationOptions options, CancellationToken cancellationToken = default) => await Engine.RunAsync(options, false, cancellationToken);
  public static async Task<MutationDiscoveryResult> DiscoverAsync(MutationOptions options, CancellationToken cancellationToken = default) => await Engine.DiscoverAsync(options, cancellationToken);
}

internal sealed record Candidate(string Id, string SiteId, string Operator, string File, int Start, int Length, int Line, int Column, string Original, string Replacement, string ReplacementToken, string SiteRuntimeId, string MutantRuntimeId, string? ResultType = null, MutationApplicationKind ApplicationKind = MutationApplicationKind.BuiltIn)
{
  public bool UseInlineSiteProbe { get; init; }
}
internal sealed record ProjectEvaluation(string[] CompileFiles, CSharpParseOptions ParseOptions, string? TargetFramework, string AssemblyName,
    string[] ProjectReferences, string[] ProjectPaths, IReadOnlyDictionary<string, string> AssemblyNamesByFile)
{
  public string? TargetPath { get; init; }
  public LinkedCompileItem[] LinkedCompileItems { get; init; } = [];
  public string[] BuildInputs { get; init; } = [];
  public bool HasUnsafeGeneratedCompileInputs { get; init; }
  public IReadOnlyDictionary<string, CSharpParseOptions> ParseOptionsByFile { get; init; } =
      new Dictionary<string, CSharpParseOptions>(StringComparer.OrdinalIgnoreCase);
}
internal sealed record LinkedCompileItem(string File, string Identity, string DefiningProjectPath, string ProjectPath);
internal sealed record CachedProjectEvaluation(string[] CompileFiles, string LanguageVersion, string[] DefineConstants, string? TargetFramework,
    string AssemblyName, string[] ProjectReferences, LinkedCompileItem[] LinkedCompileItems)
{
  public string? TargetPath { get; init; }
  public string[] BuildInputs { get; init; } = [];
  public bool HasUnsafeGeneratedCompileInputs { get; init; }
}
internal sealed record CachedTargetFrameworkProperties(string? TargetFramework, string? TargetFrameworks);
internal sealed record CachedTestDiscovery(TestIdentity[] Tests);
internal sealed record CachedCoverageMap(string[] RuntimeSiteIds, string[] Sites, string IntegrityHash);
internal sealed record CachedCompilationFile(string RelativePath, string BlobKey);
internal sealed record CachedCompilationArtifacts(CachedCompilationFile[] Files, string IntegrityHash);
internal sealed record CachedMutationExecutionResult(MutationResult Result, string IntegrityHash);
internal sealed record CandidateDiscoveryArtifact(Candidate[] Candidates, MutationResult[] UnsupportedMutations, string[] Diagnostics);
internal sealed record TestRun(string Name, bool Passed, bool Skipped, long DurationMs, string? Error)
{
  public string ProbePath { get; init; } = "";
  public string[] CoveredRuntimeSiteIds { get; init; } = [];
  public string? CoverageError { get; init; }
}
internal sealed class RunMetrics
{
  public int ProcessStarts, TestRunnerStarts, TestHostStarts, BuildInvocations, DiscoveryOperations, BaselineTests, MutationTests, WorkerRestarts, WorkerFailures;
  public void StartedProcess(string exe, IReadOnlyList<string> args)
  {
    Interlocked.Increment(ref ProcessStarts);
    if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
      if (args.Contains("test", StringComparer.OrdinalIgnoreCase))
      {
        Interlocked.Increment(ref TestRunnerStarts);
      }

      if (args.Contains("build", StringComparer.OrdinalIgnoreCase))
      {
        Interlocked.Increment(ref BuildInvocations);
      }

      if (args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
      {
        Interlocked.Increment(ref DiscoveryOperations);
      }
    }
  }
  public void StartedTestRunner() { Interlocked.Increment(ref ProcessStarts); Interlocked.Increment(ref TestRunnerStarts); }
  public void StartedTestHost(int count = 1) { Interlocked.Add(ref ProcessStarts, count); Interlocked.Add(ref TestHostStarts, count); }
}

internal sealed class ResourceLockManager
{
  private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

  public async Task<T> ExecuteAsync<T>(IReadOnlyList<string> resourceKeys, Func<Task<T>> action, CancellationToken cancellationToken)
  {
    var acquired = new List<SemaphoreSlim>();
    try
    {
      foreach (var key in resourceKeys.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
      {
        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        acquired.Add(gate);
      }
      return await action();
    }
    finally
    {
      for (var index = acquired.Count - 1; index >= 0; index--)
      {
        acquired[index].Release();
      }
    }
  }
}

internal static class Engine
{
  public static async Task<MutationDiscoveryResult> DiscoverAsync(MutationOptions opt, CancellationToken ct)
  {
    ValidateMutatorOptions(opt);
    var sw = Stopwatch.StartNew(); var metrics = new RunMetrics();
    var app = Path.GetFullPath(opt.ProjectPath);
    var testProjects = opt.TestProjects.Select(Path.GetFullPath).ToArray();
    if (testProjects.Length == 0)
    {
      throw new ArgumentException("At least one test project is required.");
    }

    if (!File.Exists(app) || opt.ProductionProjects.Any(project => !File.Exists(project)) || testProjects.Any(t => !File.Exists(t)))
    {
      throw new FileNotFoundException("Project or test project does not exist.");
    }

    var diagnostics = new List<string>();
    var unsupported = new List<MutationResult>();
    var cache = CreateCache(opt, app);
    var evaluation = await GetProductionGraph(app, opt.ProductionProjects, metrics, ct, opt.TargetFramework, cache, diagnostics);
    var candidates = await DiscoverCandidatesCached(evaluation.CompileFiles, app, opt, diagnostics, ct, unsupported, evaluation, cache);
    var testDlls = new List<string>();
    var testEvaluations = new List<ProjectEvaluation>(testProjects.Length);
    foreach (var test in testProjects)
    {
      var preRestoreEvaluation = await GetProjectGraph(test, metrics, ct, opt.TargetFramework, cache, diagnostics);
      using var compilationLocks = await AcquireCompilationLocksAsync(preRestoreEvaluation.ProjectPaths, opt.TimeoutSeconds, ct);
      var restore = await RunProcess("dotnet", ["restore", test, "--nologo", "--verbosity", "quiet"],
          Path.GetDirectoryName(test)!, null, opt.TimeoutSeconds, metrics, ct);
      if (restore.ExitCode != 0)
      {
        throw new InvalidOperationException($"Test project restore failed ({test}). " + restore.Output);
      }

      var testEvaluation = await GetProjectGraph(test, metrics, ct, opt.TargetFramework, cache, diagnostics);
      testEvaluations.Add(testEvaluation);
      var framework = opt.TargetFramework ?? testEvaluation.TargetFramework;
      var targetPath = ResolveEvaluatedTargetPath(testEvaluation, test, framework);
      var compilationKey = GetCompilationArtifactKey(test, testEvaluation, framework, out var canCacheCompilation, out var cacheBypassReason);
      var outputDirectory = Path.GetDirectoryName(targetPath)!;
      var logicalIdentity = Path.GetFullPath(test) + "|" + (framework ?? "default") + "|Debug";
      var reusedCompilation = false;
      var compilationRestore = canCacheCompilation
          ? await TryRestoreCompilationArtifacts(cache, "compilation-artifacts", logicalIdentity, compilationKey, outputDirectory,
              privateOutputDirectory: false, cancellationToken: ct)
          : (Restored: false, Diagnostic: "");
      if (compilationRestore.Restored)
      {
        diagnostics.Add(compilationRestore.Diagnostic);
        reusedCompilation = true;
      }
      else
      {
        diagnostics.Add(canCacheCompilation ? compilationRestore.Diagnostic : $"CacheBypass: compilation artifacts for '{test}' are unsafe to reuse ({cacheBypassReason}).");
      }

      if (!reusedCompilation)
      {
        var buildArgs = new List<string> { "build", test, "-c", "Debug", "--no-restore", "--nologo", "-v:q" };
        if (!string.IsNullOrWhiteSpace(framework)) { buildArgs.Add("-f"); buildArgs.Add(framework); }
        var build = await RunProcess("dotnet", buildArgs, Path.GetDirectoryName(test)!, null, opt.TimeoutSeconds, metrics, ct);
        if (build.ExitCode != 0)
        {
          throw new InvalidOperationException($"Test project build failed ({test}). " + build.Output);
        }

      }

      testDlls.Add(FindTestAssembly(targetPath, test, testEvaluation.AssemblyName, framework));
    }
    var testConsole = TestPlatformPaths.FindConsole(testProjects[0]);
    var discoveryInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { testConsole };
    var discoveryCanCache = true;
    string? unresolvedTestImport = null;
    foreach (var testProject in testProjects)
    {
      discoveryInputs.UnionWith(ProjectConfigurationInputs(testProject, out var unresolvedImport));
      unresolvedTestImport ??= unresolvedImport;
      discoveryCanCache &= unresolvedImport is null;
    }
    discoveryInputs.UnionWith(testEvaluations.SelectMany(evaluation => evaluation.CompileFiles));
    foreach (var testDll in testDlls)
    {
      var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(testDll))!;
      if (Directory.Exists(outputDirectory))
      {
        discoveryInputs.UnionWith(Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).Where(IsStableTestDiscoveryInput));
      }
    }
    var discoveryKey = ArtifactCache.ContentKey(discoveryInputs,
        ["test-discovery-v1", string.Join("|", testProjects), opt.TargetFramework ?? string.Empty, opt.TestFilter ?? string.Empty,
          EnvironmentFingerprint(), EngineVersion(), typeof(TestPlatformSession).Assembly.ManifestModule.ModuleVersionId.ToString("N")]);
    var discoveryIdentity = string.Join("|", testProjects) + "|" + (opt.TargetFramework ?? "default") + "|" + (opt.TestFilter ?? "");
    TestIdentity[] testIdentities;
    var cacheDiagnostic = $"CacheBypass: test discovery depends on an MSBuild import with unresolved external inputs ({unresolvedTestImport}).";
    CachedTestDiscovery? cachedTests = null;
    var discoveryCacheHit = false;
    if (discoveryCanCache)
    {
      var cacheRead = await cache.TryReadAsync<CachedTestDiscovery>("test-discovery", discoveryIdentity, discoveryKey, ct);
      cachedTests = cacheRead.Value;
      cacheDiagnostic = cacheRead.Diagnostic;
      discoveryCacheHit = cacheRead.Found;
    }

    if (discoveryCacheHit)
    {
      diagnostics.Add(cacheDiagnostic);
      testIdentities = cachedTests!.Tests;
    }
    else
    {
      diagnostics.Add(cacheDiagnostic);
      using var platform = new TestPlatformSession(testConsole, metrics.StartedTestRunner);
      metrics.DiscoveryOperations++;
      var discoveredTests = platform.Discover(testDlls, ct);
      metrics.StartedTestHost(testDlls.Count);
      testIdentities = discoveredTests
          .Where(t => MatchesTestFilter(t, opt.TestFilter))
          .Select(t => new TestIdentity(t.Id, t.FullyQualifiedName, t.DisplayName, 0, t.ExecutorUri, t.SourceAssembly, t.Case.Id.ToString("N")))
          .OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
      if (discoveryCanCache)
      {
        await cache.WriteAsync("test-discovery", discoveryIdentity, discoveryKey, new CachedTestDiscovery(testIdentities), ct);
      }
    }
    return new MutationDiscoveryResult(candidates.Select(c => new MutationCandidateInfo(c.Id, c.SiteId, c.Operator, c.File, c.Line, c.Column, c.Original, c.Replacement)).ToArray(), testIdentities, diagnostics, sw.ElapsedMilliseconds, metrics.ProcessStarts)
    { UnsupportedMutations = unsupported };
  }

  public static async Task<MutationRunResult> RunAsync(MutationOptions opt, bool executeMutants, CancellationToken ct)
  {
    ValidateMutatorOptions(opt);
    if (opt.MaxWorkers < 1 || opt.TimeoutSeconds < 1)
    {
      throw new ArgumentException("Workers and timeout must be positive.");
    }

    if (!Enum.IsDefined(opt.ExecutionMode))
    {
      throw new ArgumentException("Execution mode must be Auto, Isolated, or Batched.");
    }

    var app = Path.GetFullPath(opt.ProjectPath);
    var tests = opt.TestProjects.Select(Path.GetFullPath).ToArray();
    if (tests.Length == 0)
    {
      throw new ArgumentException("At least one NUnit/VSTest project is required.");
    }

    if (!File.Exists(app) || opt.ProductionProjects.Any(project => !File.Exists(project)) || tests.Any(t => !File.Exists(t)))
    {
      throw new FileNotFoundException("Project or test project does not exist.");
    }

    var output = Path.GetFullPath(opt.OutputDirectory); Directory.CreateDirectory(output);
    var sw = Stopwatch.StartNew(); var diagnostics = new List<string>();
    var compileRejected = new List<MutationResult>();
    var unsupportedMutations = new List<MutationResult>();
    var metrics = new RunMetrics();
    long discoveryMs = 0, buildMs = 0, baselineMs = 0, coverageMs = 0, generationMs = 0, evaluationMs = 0, copyMs = 0, instrumentationMs = 0, planningMs = 0, workerInitializationMs = 0, executionMs = 0, reportingMs = 0;
    var session = Path.Combine(Path.GetTempPath(), "mutation-framework", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session);
    try
    {
      var timer = Stopwatch.StartNew();
      var cache = CreateCache(opt, app);
      var evaluation = await GetProductionGraph(app, opt.ProductionProjects, metrics, ct, opt.TargetFramework, cache, diagnostics);
      var testEvaluations = new List<ProjectEvaluation>(tests.Length);
      foreach (var test in tests)
      {
        testEvaluations.Add(await GetProjectGraph(test, metrics, ct, opt.TargetFramework, cache, diagnostics));
      }
      var sourceFiles = evaluation.CompileFiles;
      evaluationMs = timer.ElapsedMilliseconds;
      var candidates = await DiscoverCandidatesCached(sourceFiles, app, opt, diagnostics, ct, unsupportedMutations, evaluation, cache);
      generationMs = timer.ElapsedMilliseconds - evaluationMs;
      timer.Restart();
      var graphProjects = evaluation.ProjectPaths.Concat(testEvaluations.SelectMany(testEvaluation => testEvaluation.ProjectPaths))
          .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      var solutionRoot = FindProjectTreeRoot(graphProjects);
      var workspaceCopy = Path.Combine(session, "workspace");
      var copiedWorkspace = CopyProject(solutionRoot, workspaceCopy);
      var appCopy = Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, Path.GetDirectoryName(app)!));
      var clonedApp = Path.Combine(appCopy, Path.GetFileName(app));
      var clonedTests = new List<string>();
      foreach (var test in tests)
      {
        clonedTests.Add(Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, test)));
      }

      copyMs = timer.ElapsedMilliseconds;
      diagnostics.Add($"WorkspaceCopy: projectRoots={graphProjects.Length}, files={copiedWorkspace.Files}, bytes={copiedWorkspace.Bytes}.");
      timer.Restart();
      var mappedFiles = sourceFiles.ToDictionary(f => f, f => Path.Combine(appCopy, Path.GetRelativePath(Path.GetDirectoryName(app)!, f)), StringComparer.OrdinalIgnoreCase);
      var linkedCompileItems = evaluation.LinkedCompileItems.Concat(testEvaluations.SelectMany(testEvaluation => testEvaluation.LinkedCompileItems))
          .DistinctBy(item => (item.File, item.DefiningProjectPath, item.ProjectPath, item.Identity)).ToArray();
      var linkedSourceMap = MaterializeLinkedSources(workspaceCopy, solutionRoot, linkedCompileItems);
      StageExternalWorkspaceConfiguration(workspaceCopy, solutionRoot, [evaluation, .. testEvaluations], linkedCompileItems, linkedSourceMap);
      foreach (var (original, mapped) in linkedSourceMap)
      {
        mappedFiles[original] = mapped;
      }
      RewriteSources(mappedFiles, candidates, Path.GetDirectoryName(app)!, workspaceCopy, evaluation.ParseOptionsByFile);
      WriteRuntimeSources(workspaceCopy, solutionRoot, evaluation.ProjectPaths, candidates);
      var compilationSourceSnapshot = SnapshotCompilationSources(workspaceCopy);
      instrumentationMs = timer.ElapsedMilliseconds;
      timer.Restart();
      var testDlls = new List<string>();
      // The isolated workspace intentionally excludes obj/bin. Restore the cloned test graph
      // before its --no-restore builds so project.assets.json and generated NuGet imports are
      // rooted at the clone. Restoring the original project does not make those paths valid.
      foreach (var clonedTest in clonedTests)
      {
        var restore = await RunProcess("dotnet", ["restore", clonedTest, "--nologo", "--verbosity", "quiet", "--ignore-failed-sources"],
            Path.GetDirectoryName(clonedTest)!, null, opt.TimeoutSeconds, metrics, ct);
        if (restore.ExitCode != 0)
        {
          throw new InvalidOperationException($"Mutation workspace restore failed ({clonedTest}). " + restore.Output);
        }
      }

      long recoveryInstrumentationMs = 0;
      var recoveryPasses = 0;
      while (true)
      {
        testDlls.Clear();
        var retryBuild = false;
        for (var testIndex = 0; testIndex < clonedTests.Count; testIndex++)
        {
          var clonedTest = clonedTests[testIndex];
          var testEvaluation = testEvaluations[testIndex];
          var framework = opt.TargetFramework ?? testEvaluation.TargetFramework;
          var buildArgs = new List<string> { "build", clonedTest, "-c", "Debug", "--no-restore", "--nologo", "-v:q" };
          if (!string.IsNullOrWhiteSpace(framework)) { buildArgs.Add("-f"); buildArgs.Add(framework); }
          var originalTargetPath = ResolveEvaluatedTargetPath(testEvaluation, tests[testIndex], framework);
          var relativeTargetPath = Path.GetRelativePath(Path.GetDirectoryName(tests[testIndex])!, originalTargetPath);
          var clonedTargetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(clonedTest)!, relativeTargetPath));
          var outputDirectory = Path.GetDirectoryName(clonedTargetPath)!;
          var compilationKey = GetMutationCompilationArtifactKey(tests[testIndex], testEvaluation, framework, candidates, opt,
              out var canCacheCompilation, out var cacheBypassReason);
          var logicalIdentity = Path.GetFullPath(tests[testIndex]) + "|" + (framework ?? "default") + "|Debug|mutation-schema";
          var compilationRestore = recoveryPasses == 0 && canCacheCompilation
              ? await TryRestoreCompilationArtifacts(cache, "mutation-compilation-artifacts", logicalIdentity, compilationKey, outputDirectory,
                  privateOutputDirectory: true, cancellationToken: ct)
              : (Restored: false, Diagnostic: "");
          if (compilationRestore.Restored)
          {
            diagnostics.Add(compilationRestore.Diagnostic);
          }
          else if (recoveryPasses != 0 || !canCacheCompilation)
          {
            diagnostics.Add($"CacheBypass: mutation compilation artifacts for '{tests[testIndex]}' are unsafe to reuse ({cacheBypassReason ?? "candidate recovery changed the schema"}).");
          }
          else
          {
            diagnostics.Add(compilationRestore.Diagnostic);
          }

          var build = compilationRestore.Restored
              ? new Proc(0, string.Empty, false)
              : await RunProcess("dotnet", buildArgs, session, null, opt.TimeoutSeconds, metrics, ct);
          if (build.ExitCode != 0)
          {
            var invalidCandidates = FindInvalidCandidates(build.Output, candidates, appCopy);
            if (invalidCandidates.Count == 0 || recoveryPasses >= 2)
            {
              throw new InvalidOperationException("Mutation-enabled project build failed without a safely attributable candidate, or the bounded recovery limit was reached. " + build.Output);
            }

            foreach (var failure in invalidCandidates)
            {
              compileRejected.Add(ToResult(failure.Candidate, MutationStatus.CompileError, [], [], 0, failure.Diagnostic));
              diagnostics.Add($"CompileError: {failure.Candidate.Id} at {failure.Candidate.File}:{failure.Candidate.Line}: {failure.Diagnostic}");
            }
            var rejectedIds = invalidCandidates.Select(failure => failure.Candidate.Id).ToHashSet(StringComparer.Ordinal);
            candidates.RemoveAll(candidate => rejectedIds.Contains(candidate.Id));
            ReindexCandidates(candidates);
            var recoveryTimer = Stopwatch.StartNew();
            foreach (var pair in mappedFiles)
            {
              File.Copy(pair.Key, pair.Value, true);
            }

            RewriteSources(mappedFiles, candidates, Path.GetDirectoryName(app)!, workspaceCopy, evaluation.ParseOptionsByFile);
            WriteRuntimeSources(workspaceCopy, solutionRoot, evaluation.ProjectPaths, candidates);
            recoveryTimer.Stop(); recoveryInstrumentationMs += recoveryTimer.ElapsedMilliseconds;
            recoveryPasses++;
            retryBuild = true;
            break;
          }

          testDlls.Add(FindTestAssembly(testEvaluation.TargetPath, tests[testIndex], testEvaluation.AssemblyName, framework, clonedTest));
        }
        if (!retryBuild)
        {
          break;
        }
      }
      buildMs = timer.ElapsedMilliseconds - recoveryInstrumentationMs;
      var generatedCompileChange = FindUnsafeGeneratedCompileChange(workspaceCopy, compilationSourceSnapshot);
      if (generatedCompileChange is not null)
      {
        diagnostics.Add($"CacheBypass: mutation compilation artifacts were not stored because the build generated or changed an untracked C# input '{generatedCompileChange}'.");
      }

      if (recoveryPasses == 0 && generatedCompileChange is null)
      {
        for (var testIndex = 0; testIndex < testDlls.Count; testIndex++)
        {
          var testEvaluation = testEvaluations[testIndex];
          var framework = opt.TargetFramework ?? testEvaluation.TargetFramework;
          var compilationKey = GetMutationCompilationArtifactKey(tests[testIndex], testEvaluation, framework, candidates, opt,
              out var canCacheCompilation, out _);
          if (canCacheCompilation)
          {
            var logicalIdentity = Path.GetFullPath(tests[testIndex]) + "|" + (framework ?? "default") + "|Debug|mutation-schema";
            await StoreCompilationArtifacts(cache, "mutation-compilation-artifacts", logicalIdentity, compilationKey,
                Path.GetDirectoryName(testDlls[testIndex])!, ct);
          }
        }
      }
      instrumentationMs += recoveryInstrumentationMs;
      timer.Restart();
      using var baselineSession = new TestPlatformSession(TestPlatformPaths.FindConsole(clonedTests[0]), metrics.StartedTestRunner);
      metrics.DiscoveryOperations++;
      var stableTestSources = testDlls.Select((assembly, index) =>
      {
        var relativeAssemblyPath = Path.GetRelativePath(Path.GetDirectoryName(clonedTests[index])!, assembly);
        var stableAssemblyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(tests[index])!, relativeAssemblyPath));
        return (Runtime: Path.GetFullPath(assembly), Stable: stableAssemblyPath);
      }).ToDictionary(pair => pair.Runtime, pair => pair.Stable, StringComparer.OrdinalIgnoreCase);
      var discoveredTestCases = baselineSession.Discover(testDlls, ct, stableTestSources);
      metrics.StartedTestHost(testDlls.Count);
      ValidateResourceLockIdentities(opt.ResourceLocksByTestId, discoveredTestCases.Select(testCase => testCase.Id));
      var testCases = discoveredTestCases
          .Where(t => MatchesTestFilter(t, opt.TestFilter))
          .OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
      var testCasesById = testCases.ToDictionary(testCase => testCase.Id, StringComparer.Ordinal);
      discoveryMs = timer.ElapsedMilliseconds;
      if (testCases.Length == 0)
      {
        throw new InvalidOperationException("Test discovery failed or returned no tests.");
      }
      if (opt.TestHostIsolationPolicy is TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup)
      {
        var unsupportedTests = testCases.Where(testCase => !SupportsSerializedRunSettings(testCase.ExecutorUri))
            .Select(testCase => testCase.Id).Order(StringComparer.Ordinal).ToArray();
        if (unsupportedTests.Length != 0)
        {
          throw new InvalidOperationException("Per-batch and per-resource-group execution currently require the NUnit VSTest executor so in-host serialization can be enforced: " +
              string.Join(", ", unsupportedTests));
        }
      }

      var resourceGroupsByTestId = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup
          ? ResourceGroupPlanner.CreateGroups(testCases.Select(testCase => testCase.Id), opt.ResourceLocksByTestId)
          : new Dictionary<string, ResourceGroup>(StringComparer.Ordinal);
      var resourceGroups = resourceGroupsByTestId.Values.DistinctBy(group => group.Id)
          .OrderBy(group => group.Id, StringComparer.Ordinal).ToArray();

      var testToSites = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
      var baselineByName = new Dictionary<string, TestRun>(StringComparer.Ordinal);
      var coverageStatuses = new SortedDictionary<string, CoverageStatus>(StringComparer.Ordinal);
      var resourceLockManager = new ResourceLockManager();
      long coverageElapsed = 0, probeSerializationMs = 0, coverageMappingMs = 0;
      timer.Restart();
      var perTestCoverageInSharedHost =
          opt.TestHostIsolationPolicy is TestHostIsolationPolicy.PerMutant or TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup &&
          testCases.All(testCase => SupportsSerializedRunSettings(testCase.ExecutorUri));
      var baselineWorkerCount = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerBatch ? 1
          : opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup ? Math.Min(opt.MaxWorkers, resourceGroups.Length)
          : !perTestCoverageInSharedHost && testCases.Length > 1 && testCases.All(testCase => opt.ResourceLocksByTestId.ContainsKey(testCase.Id))
          ? Math.Min(opt.MaxWorkers, testCases.Length)
          : 1;
      if (perTestCoverageInSharedHost)
      {
        diagnostics.Add(opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup
            ? $"BaselineCoverageCollector: collecting exact per-test site coverage for {testCases.Length} serialized NUnit tests across {resourceGroups.Length} independently hosted resource groups."
            : $"BaselineCoverageCollector: collecting exact per-test site coverage for {testCases.Length} serialized NUnit tests in one reusable testhost session.");
      }
      if (baselineWorkerCount > 1)
      {
        diagnostics.Add($"BaselineConcurrency: running the {testCases.Length} independently hosted baseline tests with at most {baselineWorkerCount} workers; all selected tests declare resource locks.");
      }

      var baselineSessions = new List<TestPlatformSession> { baselineSession };
      try
      {
        while (baselineSessions.Count < baselineWorkerCount)
        {
          baselineSessions.Add(new TestPlatformSession(TestPlatformPaths.FindConsole(clonedTests[0]), metrics.StartedTestRunner));
        }

        var baselineRuns = new ConcurrentDictionary<string, TestRun>(StringComparer.Ordinal);
        if (opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup)
        {
          var nextGroupIndex = -1;
          await Task.WhenAll(baselineSessions.Select(async platform =>
          {
            while (true)
            {
              var groupIndex = Interlocked.Increment(ref nextGroupIndex);
              if (groupIndex >= resourceGroups.Length)
              {
                return;
              }

              ct.ThrowIfCancellationRequested();
              var group = resourceGroups[groupIndex];
              var groupedTests = group.TestIds.Select(testId => testCasesById[testId]).ToArray();
              var runs = await resourceLockManager.ExecuteAsync(group.ResourceLocks,
                  () => ExecuteTestsWithPerTestCoverage(groupedTests, opt, platform, metrics, ct), ct);
              foreach (var pair in runs)
              {
                if (!baselineRuns.TryAdd(pair.Key, pair.Value))
                {
                  throw new InvalidOperationException($"Baseline test '{pair.Key}' was scheduled more than once.");
                }
              }
            }
          }));
        }
        else if (perTestCoverageInSharedHost)
        {
          var locks = testCases.SelectMany(testCase => opt.ResourceLocksByTestId.GetValueOrDefault(testCase.Id, []))
              .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
          var runs = await resourceLockManager.ExecuteAsync(locks,
              () => ExecuteTestsWithPerTestCoverage(testCases, opt, baselineSession, metrics, ct), ct);
          foreach (var pair in runs)
          {
            baselineRuns.TryAdd(pair.Key, pair.Value);
          }
        }
        else
        {
          var nextBaselineIndex = -1;
          await Task.WhenAll(baselineSessions.Select(async platform =>
          {
            while (true)
            {
              var index = Interlocked.Increment(ref nextBaselineIndex);
              if (index >= testCases.Length)
              {
                return;
              }

              ct.ThrowIfCancellationRequested();
              var testCase = testCases[index];
              Interlocked.Increment(ref metrics.BaselineTests);
              var run = await ExecuteTest(testCase, session, opt, null, platform, resourceLockManager, metrics, ct);
              if (!baselineRuns.TryAdd(testCase.Id, run))
              {
                throw new InvalidOperationException($"Baseline test '{testCase.Id}' was scheduled more than once.");
              }
            }
          }));
        }

        if (baselineRuns.Count != testCases.Length)
        {
          throw new InvalidOperationException($"Baseline execution produced {baselineRuns.Count} results for {testCases.Length} selected tests.");
        }

        foreach (var testCase in testCases)
        {
          ct.ThrowIfCancellationRequested();
          var run = baselineRuns[testCase.Id];
          baselineByName[testCase.Id] = run;
          var coverageTimer = Stopwatch.StartNew();
          ProbeRead coverage;
          try
          {
            if (run.CoverageError is not null)
            {
              throw new CoverageCollectionException(CoverageStatus.CoverageUnavailable,
                  $"CoverageUnavailable: per-test VSTest collector failed for '{testCase.DisplayName}': {run.CoverageError}");
            }
            var stableAssemblyIdentities = testDlls.Select(assembly => stableTestSources[Path.GetFullPath(assembly)]).ToArray();
            coverage = perTestCoverageInSharedHost
                ? await ReadCoverageSitesCached(run.CoveredRuntimeSiteIds, testCase.Id, stableAssemblyIdentities, candidates, cache, diagnostics, ct)
                : await ReadProbeSitesCached(run.ProbePath, candidates, testCase.Id, stableAssemblyIdentities, cache, diagnostics, ct);
          }
          catch (CoverageCollectionException ex)
          {
            coverageStatuses[testCase.Id] = ex.Status;
            diagnostics.Add(string.IsNullOrWhiteSpace(run.Error)
                ? ex.Message
                : ex.Message + " Baseline test outcome: " + run.Error);
            throw new BaselineFailureException("Coverage collection failed; mutation execution cannot continue." +
                Environment.NewLine + string.Join(Environment.NewLine, diagnostics), diagnostics);
          }

          coverageTimer.Stop(); coverageMappingMs += coverageTimer.ElapsedMilliseconds;
          probeSerializationMs += coverage.SerializationMs;
          coverageElapsed += coverageTimer.ElapsedMilliseconds + coverage.SerializationMs;
          testToSites[testCase.Id] = coverage.Sites;
          coverageStatuses[testCase.Id] = coverage.Status;
          if (!run.Passed && !run.Skipped)
          {
            diagnostics.Add($"Baseline failed: {testCase.DisplayName}: {run.Error}");
          }
        }
      }
      finally
      {
        foreach (var platform in baselineSessions.Skip(1))
        {
          platform.Dispose();
        }
      }
      baselineMs = timer.ElapsedMilliseconds - coverageElapsed; coverageMs = coverageElapsed;
      if (baselineByName.Values.Any(x => !x.Passed && !x.Skipped))
      {
        throw new BaselineFailureException("Baseline test failures prevent mutation execution.", diagnostics);
      }

      var testsById = testCasesById;
      var siteTests = new Dictionary<string, List<TestCaseDescriptor>>(StringComparer.Ordinal);
      foreach (var pair in testToSites)
      {
        foreach (var site in pair.Value) { if (!siteTests.TryGetValue(site, out var list)) { siteTests[site] = list = []; } list.Add(testsById[pair.Key]); }
      }

      var preclassified = new List<MutationResult>(compileRejected);
      var workItems = new List<MutationWorkItem>();
      foreach (var candidate in candidates)
      {
        var relevant = siteTests.GetValueOrDefault(candidate.SiteId) ?? [];
        if (relevant.Count == 0)
        {
          preclassified.Add(ToResult(candidate, MutationStatus.NoCoverage, [], [], 0));
        }
        else
        {
          workItems.Add(new(candidate.Id, relevant.Select(t => t.Id).Order(StringComparer.Ordinal).ToArray(),
              relevant.Sum(t => baselineByName[t.Id].DurationMs)));
        }
      }
      var coverageComplete = coverageStatuses.Count == testCases.Length && coverageStatuses.Values.All(status => status == CoverageStatus.CoverageSuccess);
      var executionPlan = MutationPlanner.Build(workItems, preclassified, opt.ExecutionMode, coverageComplete, opt.TimeoutSeconds, opt.MaxWorkers);
      var perTestIsolationIds = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerMutant
          ? workItems.Where(item => item.TestIds.Any(testId => opt.ResourceLocksByTestId.ContainsKey(testId) &&
                  !SupportsSerializedRunSettings(testsById[testId].ExecutorUri)))
              .SelectMany(item => item.TestIds).ToHashSet(StringComparer.Ordinal)
          : opt.TestHostIsolationPolicy is TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup
              ? new HashSet<string>(StringComparer.Ordinal)
              : testCases.Select(testCase => testCase.Id).ToHashSet(StringComparer.Ordinal);
      var perMutantIsolationIds = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerMutant
          ? workItems.SelectMany(item => item.TestIds).Where(testId => !perTestIsolationIds.Contains(testId))
              .ToHashSet(StringComparer.Ordinal)
          : new HashSet<string>(StringComparer.Ordinal);
      var mutantsFallingBackToPerTestIsolation = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerMutant
          ? workItems.Count(item => item.TestIds.Any(testId => perTestIsolationIds.Contains(testId)))
          : 0;
      if (mutantsFallingBackToPerTestIsolation != 0)
      {
        diagnostics.Add($"TestHostIsolationFallback: {mutantsFallingBackToPerTestIsolation} mutants retain per-test process isolation because one or more selected tests declare resource locks.");
      }

      planningMs = timer.ElapsedMilliseconds - coverageElapsed - baselineMs;
      var completed = new MutationResult[candidates.Count + compileRejected.Count];
      var candidateIndexById = candidates.Select((candidate, index) => (candidate.Id, index)).ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal);
      for (var rejectedIndex = 0; rejectedIndex < compileRejected.Count; rejectedIndex++)
      {
        completed[candidates.Count + rejectedIndex] = compileRejected[rejectedIndex];
      }

      var mutationResultCacheKeys = new Dictionary<string, string>(StringComparer.Ordinal);
      if (!string.IsNullOrWhiteSpace(opt.MutationResultCacheScopeKey))
      {
        if (!executeMutants || executionPlan.SelectedMode != MutationExecutionMode.Isolated ||
            opt.TestHostIsolationPolicy != TestHostIsolationPolicy.PerTest || opt.ResourceLocksByTestId.Count != 0 || !coverageComplete)
        {
          diagnostics.Add("CacheBypass: validated mutation results require RunAsync, isolated mutation execution, per-test host isolation, no declared resource locks, and complete baseline coverage.");
        }
        else
        {
          var resultCacheFingerprint = CreateMutationResultCacheFingerprint(app, evaluation, testEvaluations, testDlls,
              testCases, testToSites, candidates, opt, out var cacheBypassReason, out var fingerprintComponents);
          if (resultCacheFingerprint is null)
          {
            diagnostics.Add($"CacheBypass: validated mutation results are unsafe to reuse ({cacheBypassReason}).");
          }
          else
          {
            diagnostics.Add($"CacheFingerprint: validated-mutation-results {fingerprintComponents}");
            foreach (var item in workItems)
            {
              var candidate = candidates[candidateIndexById[item.MutantId]];
              var relevantTests = item.TestIds.Select(testId => testsById[testId]).OrderBy(testCase => testCase.Id, StringComparer.Ordinal).ToArray();
              var resultKey = CreateMutationResultCacheKey(resultCacheFingerprint, candidate, relevantTests);
              mutationResultCacheKeys[item.MutantId] = resultKey;
              var logicalIdentity = Path.GetFullPath(app) + "|" + (opt.TargetFramework ?? "default") + "|" + candidate.Id;
              var cacheRead = await cache.TryReadAsync<CachedMutationExecutionResult>("validated-mutation-results",
                  logicalIdentity, resultKey, ct);
              if (cacheRead.Found &&
                  cacheRead.Value is { } cached &&
                  cached?.Result is { } cachedResult && IsValidCachedMutationResult(cached, candidate, relevantTests))
              {
                completed[candidateIndexById[item.MutantId]] = cachedResult with
                {
                  Diagnostic = "Reused a previously validated mutation result from the content-addressed cache."
                };
                diagnostics.Add(cacheRead.Diagnostic);
              }
              else if (cacheRead.Diagnostic.StartsWith("CacheHit:", StringComparison.Ordinal))
              {
                diagnostics.Add($"CacheInvalidated: validated-mutation-results {resultKey} (cached result failed identity or integrity validation)");
              }
              else
              {
                diagnostics.Add(cacheRead.Diagnostic);
              }
            }
          }
        }
      }

      var batchesToExecute = executionPlan.Batches.Where(batch => batch.MutantIds.Any(mutantId =>
          completed[candidateIndexById[mutantId]] is null)).ToArray();
      var mutationWorkUnits = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup
          ? batchesToExecute.SelectMany(batch => ResourceGroupPlanner.SplitBatch(batch, resourceGroupsByTestId)).ToArray()
          : batchesToExecute;
      var activeResourceGroupCount = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup
          ? mutationWorkUnits.SelectMany(batch => batch.TestIds).Select(testId => resourceGroupsByTestId[testId].Id)
              .Distinct(StringComparer.Ordinal).Count()
          : 0;
      var runCanceled = false;
      timer.Restart();
      var effectiveWorkerCount = executeMutants
          ? opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup
              ? Math.Min(Math.Min(opt.MaxWorkers, activeResourceGroupCount), mutationWorkUnits.Length)
              : GetEffectiveWorkerCount(opt.MaxWorkers, mutationWorkUnits, opt.TestHostIsolationPolicy, opt.ResourceLocksByTestId)
          : 0;
      if (opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup)
      {
        diagnostics.Add($"ResourceGroupScheduling: split {batchesToExecute.Length} coverage batches into {mutationWorkUnits.Length} process-isolated resource-group work units across {activeResourceGroupCount} covered groups, bounded by {effectiveWorkerCount} workers.");
      }
      if (opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerBatch && effectiveWorkerCount < Math.Min(opt.MaxWorkers, mutationWorkUnits.Length))
      {
        diagnostics.Add($"ResourceConstrainedConcurrency: reduced mutation workers from {Math.Min(opt.MaxWorkers, batchesToExecute.Length)} to {effectiveWorkerCount} because every per-batch execution shares a declared resource lock.");
      }
      var workerBusy = new long[effectiveWorkerCount];
      if (!executeMutants)
      {
        foreach (var classifiedResult in executionPlan.PreclassifiedMutants)
        {
          completed[candidateIndexById[classifiedResult.Id]] = classifiedResult;
        }

        foreach (var item in workItems)
        {
          var c = candidates[candidateIndexById[item.MutantId]];
          completed[candidateIndexById[item.MutantId]] = ToResult(c, MutationStatus.Pending,
              item.TestIds.Select(id => testsById[id].DisplayName).ToArray(), [], 0);
        }
      }
      else
      {
        foreach (var classifiedResult in executionPlan.PreclassifiedMutants)
        {
          completed[candidateIndexById[classifiedResult.Id]] = classifiedResult;
        }

        var nextBatch = -1;
        var workers = Enumerable.Range(0, workerBusy.Length).Select(async workerIndex =>
        {
          var busy = Stopwatch.StartNew();
          var initializationTimer = Stopwatch.StartNew();
          var workerSession = new TestPlatformSession(TestPlatformPaths.FindConsole(clonedTests[0]), metrics.StartedTestRunner);
          Interlocked.Add(ref workerInitializationMs, initializationTimer.ElapsedMilliseconds);

          try
          {
            while (true)
            {
              var batchIndex = Interlocked.Increment(ref nextBatch);
              if (batchIndex >= mutationWorkUnits.Length) { busy.Stop(); workerBusy[workerIndex] = busy.ElapsedMilliseconds; return; }
              ct.ThrowIfCancellationRequested();
              var batch = mutationWorkUnits[batchIndex];
              if ((opt.TestHostIsolationPolicy is TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup) &&
                  executionPlan.SelectedMode == MutationExecutionMode.Batched)
              {
                var batchTests = batch.TestIds.Select(testId => testsById[testId]).OrderBy(testCase => testCase.Id, StringComparer.Ordinal).ToArray();
                var activeMutationIds = string.Join(",", batch.MutantIds.Select(mutantId =>
                    candidates[candidateIndexById[mutantId]].MutantRuntimeId).Order(StringComparer.Ordinal));
                var locks = batch.TestIds.SelectMany(testId => opt.ResourceLocksByTestId.GetValueOrDefault(testId, []))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                Interlocked.Add(ref metrics.MutationTests, batchTests.Length);
                var mixedRuns = await resourceLockManager.ExecuteAsync(locks,
                    () => ExecuteTestsInSharedMutationHost(batchTests, opt, activeMutationIds, workerSession, metrics, ct, serializeTests: true), ct);
                var runsByTestId = batchTests.Zip(mixedRuns).ToDictionary(pair => pair.First.Id, pair => pair.Second, StringComparer.Ordinal);
                var batchFailed = false;
                foreach (var mutantId in batch.MutantIds)
                {
                  var candidate = candidates[candidateIndexById[mutantId]];
                  var relevant = batch.TestToMutantId.Where(pair => pair.Value == mutantId)
                      .Select(pair => (Test: testsById[pair.Key], Run: runsByTestId[pair.Key])).ToArray();
                  var infrastructure = relevant.FirstOrDefault(pair => pair.Run.Error?.StartsWith("INFRASTRUCTURE:", StringComparison.Ordinal) == true);
                  var timeout = relevant.FirstOrDefault(pair => pair.Run.Error?.StartsWith("TIMEOUT:", StringComparison.Ordinal) == true);
                  var uncertain = relevant.FirstOrDefault(pair => pair.Run.Skipped || pair.Run.Error?.StartsWith("TEST_FAILURE_UNATTRIBUTED:", StringComparison.Ordinal) == true);
                  var killed = relevant.Where(pair => pair.Run.Error?.StartsWith("TEST_FAILURE:", StringComparison.Ordinal) == true).ToArray();
                  var status = infrastructure.Test is not null ? MutationStatus.InfrastructureError
                      : timeout.Test is not null ? MutationStatus.Timeout
                      : uncertain.Test is not null ? MutationStatus.Inconclusive
                      : killed.Length != 0 ? MutationStatus.Killed
                      : MutationStatus.Survived;
                  var diagnostic = infrastructure.Run?.Error ?? timeout.Run?.Error ?? uncertain.Run?.Error ?? killed.FirstOrDefault().Run?.Error;
                  completed[candidateIndexById[mutantId]] = ToResult(candidate, status,
                      relevant.Select(pair => pair.Test.DisplayName).ToArray(), killed.Select(pair => pair.Test.DisplayName).ToArray(),
                      relevant.Sum(pair => pair.Run.DurationMs), diagnostic);
                  batchFailed |= status is MutationStatus.InfrastructureError or MutationStatus.Timeout;
                }

                if (batchFailed)
                {
                  workerSession.Dispose();
                  var restartTimer = Stopwatch.StartNew();
                  workerSession = new TestPlatformSession(TestPlatformPaths.FindConsole(clonedTests[0]), metrics.StartedTestRunner);
                  restartTimer.Stop();
                  Interlocked.Add(ref workerInitializationMs, restartTimer.ElapsedMilliseconds);
                  Interlocked.Increment(ref metrics.WorkerRestarts);
                }
                continue;
              }

              foreach (var mutantId in batch.MutantIds)
              {
                ct.ThrowIfCancellationRequested();
                var i = candidateIndexById[mutantId]; var c = candidates[i];
                if (completed[i] is not null)
                {
                  continue;
                }
                var relevant = batch.TestToMutantId.Where(pair => pair.Value == mutantId).Select(pair => testsById[pair.Key]).OrderBy(test => test.Id, StringComparer.Ordinal).ToArray();
                var kill = new List<string>(); long duration = 0; var status = MutationStatus.Survived; string? error = null;
                if (opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerMutant &&
                    relevant.All(testCase => !perTestIsolationIds.Contains(testCase.Id)))
                {
                  var resourceLocks = relevant.SelectMany(testCase => opt.ResourceLocksByTestId.GetValueOrDefault(testCase.Id, []))
                      .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                  var runs = await resourceLockManager.ExecuteAsync(resourceLocks,
                      () => ExecuteTestsInSharedMutationHost(relevant, opt, c.MutantRuntimeId, workerSession, metrics, ct,
                          serializeTests: resourceLocks.Length != 0, stopAfterAttributableFailure: true), ct);
                  Interlocked.Add(ref metrics.MutationTests,
                      runs.Count(run => run.Error?.StartsWith("BAILED:", StringComparison.Ordinal) != true));
                  duration = runs.Sum(run => run.DurationMs);
                  var infrastructureFailure = runs.FirstOrDefault(run => run.Error?.StartsWith("INFRASTRUCTURE:", StringComparison.Ordinal) == true);
                  var timeout = runs.FirstOrDefault(run => run.Error?.StartsWith("TIMEOUT:", StringComparison.Ordinal) == true);
                  var killingRuns = runs.Where(run => run.Error?.StartsWith("TEST_FAILURE:", StringComparison.Ordinal) == true).ToArray();
                  var uncertain = runs.FirstOrDefault(run =>
                      run.Error?.StartsWith("BAILED:", StringComparison.Ordinal) != true &&
                      (run.Skipped || run.Error?.StartsWith("TEST_FAILURE_UNATTRIBUTED:", StringComparison.Ordinal) == true));
                  if (infrastructureFailure != null)
                  {
                    status = MutationStatus.InfrastructureError; error = infrastructureFailure.Error;
                  }
                  else if (timeout != null)
                  {
                    status = MutationStatus.Timeout; error = timeout.Error;
                  }
                  else if (killingRuns.Length != 0)
                  {
                    status = MutationStatus.Killed;
                    error = killingRuns[0].Error;
                    kill.AddRange(killingRuns.Select(run => run.Name));
                  }
                  else if (uncertain != null)
                  {
                    status = MutationStatus.Inconclusive;
                    error = uncertain.Error ?? "INCONCLUSIVE: Selected test was skipped.";
                  }
                  else
                  {
                    status = MutationStatus.Survived;
                  }
                }
                else
                {
                  foreach (var testCase in relevant)
                  {
                    Interlocked.Increment(ref metrics.MutationTests);
                    var run = await ExecuteTest(testCase, session, opt, c.MutantRuntimeId, workerSession, resourceLockManager, metrics, ct); duration += run.DurationMs;
                    if (run.Skipped) { status = MutationStatus.Inconclusive; error = "Selected test was skipped."; break; }
                    if (!run.Passed)
                    {
                      if (run.Error != null && run.Error.StartsWith("TEST_FAILURE:", StringComparison.Ordinal)) { status = MutationStatus.Killed; error = run.Error; kill.Add(testCase.DisplayName); break; }
                      if (run.Error != null && run.Error.StartsWith("TEST_FAILURE_UNATTRIBUTED:", StringComparison.Ordinal)) { status = MutationStatus.Inconclusive; error = run.Error; break; }
                      status = run.Error?.StartsWith("TIMEOUT:", StringComparison.Ordinal) == true ? MutationStatus.Timeout : MutationStatus.InfrastructureError; error = run.Error; break;
                    }
                  }
                }
                var mutationResult = ToResult(c, status, relevant.Select(test => test.DisplayName).ToArray(), kill, duration, error);
                completed[i] = mutationResult;
                if (status is (MutationStatus.Killed or MutationStatus.Survived) && mutationResultCacheKeys.TryGetValue(mutantId, out var resultKey))
                {
                  var logicalIdentity = Path.GetFullPath(app) + "|" + (opt.TargetFramework ?? "default") + "|" + c.Id;
                  var cachedResult = new CachedMutationExecutionResult(mutationResult, MutationResultIntegrity(mutationResult));
                  await cache.WriteAsync("validated-mutation-results", logicalIdentity, resultKey, cachedResult, ct);
                }
                if (status is MutationStatus.InfrastructureError or MutationStatus.Timeout)
                {
                  if (status == MutationStatus.InfrastructureError)
                  {
                    Interlocked.Increment(ref metrics.WorkerFailures);
                  }

                  workerSession.Dispose();
                  var restartTimer = Stopwatch.StartNew();
                  workerSession = new TestPlatformSession(TestPlatformPaths.FindConsole(clonedTests[0]), metrics.StartedTestRunner);
                  restartTimer.Stop();
                  Interlocked.Add(ref workerInitializationMs, restartTimer.ElapsedMilliseconds);
                  Interlocked.Increment(ref metrics.WorkerRestarts);
                }
              }
            }
          }
          finally { workerSession.Dispose(); }
        }).ToArray();
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException)
        {
          runCanceled = true;
          for (var i = 0; i < candidates.Count; i++)
          {
            completed[i] ??= ToResult(candidates[i], MutationStatus.Inconclusive, [], [], 0, "Run canceled before this mutant completed.");
          }
        }
        catch (Exception ex)
        {
          Interlocked.Increment(ref metrics.WorkerFailures);
          diagnostics.Add("Mutation worker failed: " + ex.Message);
          for (var i = 0; i < candidates.Count; i++)
          {
            completed[i] ??= ToResult(candidates[i], MutationStatus.InfrastructureError, [], [], 0, "Worker failed before this mutant completed: " + ex.Message);
          }
        }
        executionMs = timer.ElapsedMilliseconds;
      }
      timer.Restart();
      var ordered = completed.OrderBy(x => x.SourceFile, StringComparer.Ordinal).ThenBy(x => x.Line).ThenBy(x => x.Column).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
      var killed = ordered.Count(x => x.Status == MutationStatus.Killed); var survived = ordered.Count(x => x.Status == MutationStatus.Survived); var denominator = killed + survived;
      var timings = new PhaseTimings(discoveryMs, buildMs, baselineMs, coverageMs, generationMs, executionMs, 0)
      {
        ProbeSerializationMs = probeSerializationMs,
        CoverageMappingMs = coverageMappingMs,
        ProjectEvaluationMs = evaluationMs,
        ProjectCopyMs = copyMs,
        InstrumentationMs = instrumentationMs,
        MutationPlanningMs = Math.Max(0, planningMs),
        WorkerInitializationMs = workerInitializationMs
      };
      var uncovered = ordered.Count(x => x.Status == MutationStatus.NoCoverage);
      var busyMs = workerBusy.Sum();
      var coveredCount = workItems.Count;
      var counters = new ExecutionCounters(ordered.Length, coveredCount, uncovered, testCases.Length, metrics.BaselineTests, metrics.MutationTests, metrics.TestRunnerStarts, metrics.DiscoveryOperations, metrics.BuildInvocations, metrics.WorkerRestarts,
          metrics.WorkerFailures, executionPlan.Batches.Count, executionPlan.Statistics.AverageMutantsPerBatch, busyMs,
          Math.Max(0, executionMs * workerBusy.Length - busyMs))
      { TestHostProcessStarts = metrics.TestHostStarts };
      var overallStatus = !executeMutants ? "Planned" : runCanceled ? "Canceled" : ordered.Any(x => x.Status is MutationStatus.InfrastructureError or MutationStatus.Timeout or MutationStatus.Inconclusive or MutationStatus.CompileError) ? "CompletedWithErrors" : "Completed";
      var result = new MutationRunResult(overallStatus, ordered, killed, survived, uncovered, denominator == 0 ? null : (double)killed * 100 / denominator, timings, sw.ElapsedMilliseconds, testToSites, diagnostics, Path.Combine(output, "mutation-report.json"), Path.Combine(output, "index.html"))
      {
        Counters = counters,
        UnsupportedMutations = unsupportedMutations,
        ProcessStarts = metrics.ProcessStarts,
        ExecutionPlan = executionPlan,
        Tests = testCases.Select(t => new TestIdentity(t.Id, t.FullyQualifiedName, t.DisplayName, baselineByName[t.Id].DurationMs, t.ExecutorUri, t.SourceAssembly, t.Case.Id.ToString("N"))).ToArray(),
        ExecutionProfiles = testCases.Select(testCase =>
        {
          var usesPerTestIsolation = perTestIsolationIds.Contains(testCase.Id);
          var usesPerMutantIsolation = perMutantIsolationIds.Contains(testCase.Id);
          var (effectiveIsolationPolicy, timeoutPolicy) = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerBatch
              ? (TestHostIsolationPolicy.PerBatch, TestRunTimeoutPolicy.AggregatePerBatch)
              : opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup
                  ? (TestHostIsolationPolicy.PerResourceGroup, TestRunTimeoutPolicy.AggregatePerBatch)
              : ResolveTestExecutionPolicies(usesPerTestIsolation, usesPerMutantIsolation);
          return new TestExecutionProfile(testCase.Id,
              baselineByName[testCase.Id].DurationMs, testToSites.GetValueOrDefault(testCase.Id, []),
              GetFixtureAffinity(testCase.FullyQualifiedName), usesPerTestIsolation,
              opt.ResourceLocksByTestId.GetValueOrDefault(testCase.Id, []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
              opt.TimeoutSeconds, opt.TestHostIsolationPolicy is TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup
                  ? [MutationExecutionMode.Batched]
                  : [MutationExecutionMode.Isolated, MutationExecutionMode.Batched])
          {
            TestHostIsolationPolicy = effectiveIsolationPolicy,
            TimeoutPolicy = timeoutPolicy,
            TestsSerializedInSharedHost = opt.TestHostIsolationPolicy is TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup ||
                effectiveIsolationPolicy == TestHostIsolationPolicy.PerMutant && opt.ResourceLocksByTestId.ContainsKey(testCase.Id),
            ResourceIsolationGroup = resourceGroupsByTestId.GetValueOrDefault(testCase.Id)?.Id
          };
        }).ToArray(),
        CoverageStatuses = coverageStatuses,
      };
      var jsonOptions = new JsonSerializerOptions { WriteIndented = true }; jsonOptions.Converters.Add(new JsonStringEnumConverter());
      if (opt.Report.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("json", StringComparer.OrdinalIgnoreCase))
      {
        await File.WriteAllTextAsync(result.JsonReportPath, JsonSerializer.Serialize(result, jsonOptions), ct);
      }

      if (opt.Report.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("html", StringComparer.OrdinalIgnoreCase))
      {
        await File.WriteAllTextAsync(result.HtmlReportPath, Html(result), ct);
      }

      reportingMs = timer.ElapsedMilliseconds;
      result = result with { Timings = timings with { ReportingMs = reportingMs }, TotalDurationMs = sw.ElapsedMilliseconds };
      if (File.Exists(result.JsonReportPath))
      {
        await File.WriteAllTextAsync(result.JsonReportPath, JsonSerializer.Serialize(result, jsonOptions), ct);
      }

      if (File.Exists(result.HtmlReportPath))
      {
        await File.WriteAllTextAsync(result.HtmlReportPath, Html(result), ct);
      }

      Console.WriteLine($"Mutants: {ordered.Length}; killed: {killed}; survived: {survived}; no coverage: {result.NoCoverageCount}; score: {(result.MutationScore is null ? "n/a" : result.MutationScore.Value.ToString("F1") + "%")}");
      return result;
    }
    finally { try { Directory.Delete(session, true); } catch { } }
  }

  private static string? CreateMutationResultCacheFingerprint(string app, ProjectEvaluation productionEvaluation,
      IReadOnlyList<ProjectEvaluation> testEvaluations, IReadOnlyList<string> testDlls, IReadOnlyList<TestCaseDescriptor> testCases,
      IReadOnlyDictionary<string, IReadOnlyList<string>> testToSites, IReadOnlyList<Candidate> candidates, MutationOptions options,
      out string? bypassReason, out string fingerprintComponents)
  {
    bypassReason = null;
    fingerprintComponents = string.Empty;
    var projectPaths = productionEvaluation.ProjectPaths.Concat(testEvaluations.SelectMany(evaluation => evaluation.ProjectPaths))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var inputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var project in projectPaths)
    {
      var configurationInputs = ProjectConfigurationInputs(project, out var unresolvedImport);
      if (unresolvedImport is not null)
      {
        bypassReason = $"project '{project}' has unresolved imports ({unresolvedImport})";
        return null;
      }

      inputPaths.UnionWith(configurationInputs);
      var assetsFile = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
      if (!File.Exists(assetsFile))
      {
        bypassReason = $"project '{project}' has no restored dependency assets";
        return null;
      }
      inputPaths.Add(assetsFile);
      try
      {
        inputPaths.UnionWith(PackageAssetInputs(assetsFile));
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
      {
        bypassReason = $"project '{project}' dependency assets cannot be fingerprinted ({exception.Message})";
        return null;
      }
    }
    inputPaths.UnionWith(productionEvaluation.CompileFiles);
    inputPaths.UnionWith(productionEvaluation.BuildInputs);
    foreach (var testEvaluation in testEvaluations)
    {
      inputPaths.UnionWith(testEvaluation.CompileFiles);
      inputPaths.UnionWith(testEvaluation.BuildInputs);
    }

    var outputFingerprints = new List<string>();
    foreach (var testDll in testDlls.Order(StringComparer.OrdinalIgnoreCase))
    {
      var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(testDll))!;
      try
      {
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                     .Where(IsStableTestDiscoveryInput)
                     .Where(IsRuntimeDependencyFile)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
          outputFingerprints.Add(Path.GetFileName(testDll) + "|" + Path.GetRelativePath(outputDirectory, file).Replace('\\', '/') + "|" + HashFileContent(file));
        }
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        bypassReason = $"test output for '{testDll}' cannot be fingerprinted ({exception.Message})";
        return null;
      }
    }

    var mutatorFingerprint = options.Registry?.Fingerprint ?? string.Join(";",
        (options.Mutators ?? MutatorRegistry.CreateDefault().Mutators.Select(mutator => mutator.Id)
            .ToHashSet(StringComparer.Ordinal)).Order(StringComparer.Ordinal));
    var values = new List<string>
    {
      "validated-mutation-results-v1", Path.GetFullPath(app), options.TargetFramework ?? string.Empty,
      options.MutationResultCacheScopeKey!, options.TestFilter ?? string.Empty, options.Include ?? string.Empty,
      options.Exclude ?? string.Empty, options.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
      options.TestHostIsolationPolicy.ToString(), EnvironmentFingerprint(), EngineVersion(),
      typeof(TestPlatformSession).Assembly.ManifestModule.ModuleVersionId.ToString("N"), mutatorFingerprint,
      RuntimeSource(candidates.Select(candidate => candidate.SiteRuntimeId).Distinct(StringComparer.Ordinal).Count()),
      string.Join(";", projectPaths.Order(StringComparer.OrdinalIgnoreCase)),
      string.Join(";", testCases.OrderBy(testCase => testCase.Id, StringComparer.Ordinal)
          .Select(testCase => string.Join("|", testCase.Id, testCase.FullyQualifiedName, testCase.DisplayName,
              testCase.ExecutorUri, testCase.SourceAssembly))),
      string.Join(";", testToSites.OrderBy(pair => pair.Key, StringComparer.Ordinal)
          .Select(pair => pair.Key + "=" + string.Join(",", pair.Value.Order(StringComparer.Ordinal)))),
      string.Join(";", productionEvaluation.ParseOptionsByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
          .Select(pair => pair.Key + "=" + pair.Value.LanguageVersion + ":" + string.Join(",", pair.Value.PreprocessorSymbolNames.Order(StringComparer.Ordinal))))
    };
    values.AddRange(outputFingerprints.Order(StringComparer.Ordinal));
    string inputFingerprint;
    try
    {
      inputFingerprint = ArtifactCache.ContentKey(inputPaths, []);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      bypassReason = $"project input cannot be fingerprinted ({exception.Message})";
      return null;
    }
    var valueFingerprint = ArtifactCache.ContentKey([], values);
    var outputFingerprint = ArtifactCache.ContentKey([], outputFingerprints.Order(StringComparer.Ordinal));
    fingerprintComponents = $"inputs={inputFingerprint[..12]} values={valueFingerprint[..12]} outputs={outputFingerprint[..12]}";
    return ArtifactCache.ContentKey([], ["validated-mutation-result-fingerprint-v1", inputFingerprint, valueFingerprint, outputFingerprint]);
  }

  private static string CreateMutationResultCacheKey(string fingerprint, Candidate candidate, IReadOnlyList<TestCaseDescriptor> tests) =>
      ArtifactCache.ContentKey([], ["validated-mutation-result-v1", fingerprint, candidate.Id, candidate.SiteId,
          candidate.Operator, candidate.File, candidate.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
          candidate.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), candidate.Original, candidate.Replacement,
          candidate.ReplacementToken, candidate.SiteRuntimeId, candidate.MutantRuntimeId,
          string.Join(";", tests.Select(test => string.Join("|", test.Id, test.FullyQualifiedName, test.DisplayName,
              test.ExecutorUri, test.SourceAssembly)))]);

  private static bool IsValidCachedMutationResult(CachedMutationExecutionResult cached, Candidate candidate,
      IReadOnlyList<TestCaseDescriptor> relevantTests)
  {
    var result = cached.Result;
    var coveredTestNames = relevantTests.Select(test => test.DisplayName).ToArray();
    return (result.Status is MutationStatus.Killed or MutationStatus.Survived) &&
        string.Equals(result.Id, candidate.Id, StringComparison.Ordinal) &&
        string.Equals(result.SiteId, candidate.SiteId, StringComparison.Ordinal) &&
        string.Equals(result.Operator, candidate.Operator, StringComparison.Ordinal) &&
        string.Equals(result.SourceFile, candidate.File, StringComparison.Ordinal) &&
        result.Line == candidate.Line && result.Column == candidate.Column &&
        string.Equals(result.Original, candidate.Original, StringComparison.Ordinal) &&
        string.Equals(result.Replacement, candidate.Replacement, StringComparison.Ordinal) &&
        result.CoveringTests.SequenceEqual(coveredTestNames, StringComparer.Ordinal) &&
        result.KillingTests.All(coveredTestNames.Contains) &&
        (result.Status == MutationStatus.Killed) == (result.KillingTests.Count > 0) &&
        string.Equals(cached.IntegrityHash, MutationResultIntegrity(result), StringComparison.Ordinal);
  }

  private static string MutationResultIntegrity(MutationResult result) => ArtifactCache.ContentKey([], [
      "validated-mutation-result-record-v1", result.Id, result.SiteId, result.Operator, result.SourceFile,
      result.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Column.ToString(System.Globalization.CultureInfo.InvariantCulture),
      result.Original, result.Replacement, result.Status.ToString(),
      string.Join(";", result.CoveringTests), string.Join(";", result.KillingTests),
      result.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Diagnostic ?? string.Empty
    ]);

  private static string HashFileContent(string path)
  {
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
  }

  private static bool IsRuntimeDependencyFile(string path) =>
      Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".so" or ".dylib" or ".json" or ".config" or ".resources";

  private static MutationResult ToResult(Candidate c, MutationStatus s, IReadOnlyList<string> tests, IReadOnlyList<string> killing, long ms, string? error = null) => new(c.Id, c.SiteId, c.Operator, c.File, c.Line, c.Column, c.Original, c.Replacement, s, tests, killing, ms, error);

  private static void ValidateMutatorOptions(MutationOptions options)
  {
    if (options.TestHostIsolationPolicy is not (TestHostIsolationPolicy.PerTest or TestHostIsolationPolicy.PerMutant or
        TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup))
    {
      throw new ArgumentOutOfRangeException(nameof(options.TestHostIsolationPolicy));
    }

    if (options.TestHostIsolationPolicy == TestHostIsolationPolicy.PerBatch && options.ExecutionMode != MutationExecutionMode.Batched)
    {
      throw new ArgumentException("Per-batch testhost reuse requires explicit batched execution with complete coverage.", nameof(options));
    }

    if (options.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup && options.ExecutionMode != MutationExecutionMode.Batched)
    {
      throw new ArgumentException("Per-resource-group testhost reuse requires explicit batched execution with complete coverage.", nameof(options));
    }

    if (options.Registry is not null && options.Mutators is not null)
    {
      throw new ArgumentException("Set either an explicit mutator registry or built-in mutator IDs, not both.");
    }
  }

  internal static (TestHostIsolationPolicy Isolation, TestRunTimeoutPolicy Timeout) ResolveTestExecutionPolicies(bool perTest, bool perMutant) =>
      (perTest, perMutant) switch
      {
        (true, true) => (TestHostIsolationPolicy.Mixed, TestRunTimeoutPolicy.Mixed),
        (false, true) => (TestHostIsolationPolicy.PerMutant, TestRunTimeoutPolicy.AggregatePerMutant),
        (true, false) => (TestHostIsolationPolicy.PerTest, TestRunTimeoutPolicy.PerTest),
        // In per-mutant mode, an uncovered test has no isolation work to perform, but its
        // profile still needs to reflect the configured execution policy consistently.
        (false, false) => (TestHostIsolationPolicy.PerMutant, TestRunTimeoutPolicy.AggregatePerMutant)
      };

  internal static bool SupportsSerializedRunSettings(string executorUri) =>
      string.Equals(executorUri, "executor://nunit3testexecutor/", StringComparison.OrdinalIgnoreCase);

  internal static int GetEffectiveWorkerCount(int maxWorkers, IReadOnlyList<MutationBatch> batches,
      TestHostIsolationPolicy isolationPolicy, IReadOnlyDictionary<string, IReadOnlyList<string>> resourceLocksByTestId)
  {
    if (batches.Count == 0)
    {
      return 0;
    }

    var workerCount = Math.Min(maxWorkers, batches.Count);
    if (workerCount <= 1 || isolationPolicy != TestHostIsolationPolicy.PerBatch)
    {
      return workerCount;
    }

    var batchLocks = batches.Select(batch => batch.TestIds
        .SelectMany(testId => resourceLocksByTestId.GetValueOrDefault(testId, []))
        .ToHashSet(StringComparer.Ordinal)).ToArray();
    for (var left = 0; left < batchLocks.Length; left++)
    {
      for (var right = left + 1; right < batchLocks.Length; right++)
      {
        if (!batchLocks[left].Overlaps(batchLocks[right]))
        {
          return workerCount;
        }
      }
    }

    return 1;
  }

  internal static void ValidateResourceLockIdentities(IReadOnlyDictionary<string, IReadOnlyList<string>> resourceLocks,
      IEnumerable<string> discoveredTestIds)
  {
    ArgumentNullException.ThrowIfNull(resourceLocks);
    ArgumentNullException.ThrowIfNull(discoveredTestIds);
    var knownIds = discoveredTestIds.ToHashSet(StringComparer.Ordinal);
    var unknown = resourceLocks.Keys.Where(testId => !knownIds.Contains(testId)).Order(StringComparer.Ordinal).ToArray();
    if (unknown.Length != 0)
    {
      throw new ArgumentException($"Resource locks refer to test identities not found in the selected test projects: {string.Join(", ", unknown)}",
          nameof(resourceLocks));
    }

    var invalidLocks = resourceLocks.Where(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
        pair.Value.Count == 0 || pair.Value.Any(string.IsNullOrWhiteSpace)).Take(1).ToArray();
    if (invalidLocks.Length != 0)
    {
      throw new ArgumentException($"Resource locks for test '{invalidLocks[0].Key}' must include at least one non-empty lock name.",
          nameof(resourceLocks));
    }
  }

  private static string? GetFixtureAffinity(string fullyQualifiedName)
  {
    var separator = fullyQualifiedName.LastIndexOf('.');
    return separator > 0 ? fullyQualifiedName[..separator] : null;
  }

  private static bool MatchesTestFilter(TestCaseDescriptor test, string? filter) =>
      string.IsNullOrWhiteSpace(filter) || filter.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
          .Any(term => test.FullyQualifiedName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
              test.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));

  private static async Task<TestRun> ExecuteTest(TestCaseDescriptor test, string session, MutationOptions opt, string? active, TestPlatformSession platform, ResourceLockManager locks, RunMetrics metrics, CancellationToken ct)
  {
    string? probe = null;
    if (active is null)
    {
      var resultDir = Path.Combine(session, "results", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(resultDir);
      probe = Path.Combine(resultDir, "probes.json");
    }

    var timer = Stopwatch.StartNew();
    var resourceLocks = opt.ResourceLocksByTestId.GetValueOrDefault(test.Id, []);
    var result = await locks.ExecuteAsync(resourceLocks,
        () => Task.Run(() => platform.Execute(test, probe, active, opt.TimeoutSeconds, () => metrics.StartedTestHost(), ct), CancellationToken.None), ct);
    timer.Stop();
    return new(test.DisplayName, result.Passed, result.Skipped, timer.ElapsedMilliseconds, result.Error) { ProbePath = probe ?? string.Empty };
  }

  private static async Task<IReadOnlyList<TestRun>> ExecuteTestsInSharedMutationHost(TestCaseDescriptor[] tests,
      MutationOptions options, string active, TestPlatformSession platform, RunMetrics metrics, CancellationToken cancellationToken,
      bool serializeTests = false, bool stopAfterAttributableFailure = false)
  {
    var totalTimeout = (int)Math.Min(int.MaxValue / 1000L, (long)options.TimeoutSeconds * tests.Length);
    var results = await Task.Run(() => platform.ExecuteMany(tests, null, active, totalTimeout,
        () => metrics.StartedTestHost(), cancellationToken, serializeTests,
        stopAfterAttributableFailure: stopAfterAttributableFailure), CancellationToken.None);
    return tests.Select(test =>
    {
      var result = results[test.Id];
      return new TestRun(test.DisplayName, result.Passed, result.Skipped, result.DurationMs, result.Error)
      { ProbePath = string.Empty };
    }).ToArray();
  }

  private static async Task<IReadOnlyDictionary<string, TestRun>> ExecuteTestsWithPerTestCoverage(TestCaseDescriptor[] tests,
      MutationOptions options, TestPlatformSession platform, RunMetrics metrics, CancellationToken cancellationToken)
  {
    var aggregateTimeout = (int)Math.Min(int.MaxValue, (long)options.TimeoutSeconds * tests.Length);
    foreach (var _ in tests)
    {
      Interlocked.Increment(ref metrics.BaselineTests);
    }

    var results = await Task.Run(() => platform.ExecuteMany(tests, null, null, aggregateTimeout,
        () => metrics.StartedTestHost(), cancellationToken, serializeTests: true, capturePerTestCoverage: true),
        CancellationToken.None);
    return tests.ToDictionary(test => test.Id, test =>
    {
      var result = results[test.Id];
      return new TestRun(test.DisplayName, result.Passed, result.Skipped, result.DurationMs, result.Error)
      {
        CoveredRuntimeSiteIds = result.CoveredRuntimeSiteIds ?? [],
        CoverageError = result.CoverageError
      };
    }, StringComparer.Ordinal);
  }

  private static async Task<Proc> RunProcess(string exe, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string?>? env, int timeout, RunMetrics metrics, CancellationToken ct)
  {
    var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in args)
    {
      psi.ArgumentList.Add(arg);
    }

    if (env != null)
    {
      foreach (var kv in env)
      {
        psi.Environment[kv.Key] = kv.Value;
      }
    }

    using var p = new Process { StartInfo = psi, EnableRaisingEvents = true }; p.Start(); metrics.StartedProcess(exe, args); var stdout = p.StandardOutput.ReadToEndAsync(ct); var stderr = p.StandardError.ReadToEndAsync(ct);
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct); timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout)); var timed = false;
    try { await p.WaitForExitAsync(timeoutCts.Token); }
    catch (OperationCanceledException)
    {
      timed = !ct.IsCancellationRequested; try { p.Kill(true); } catch { }
      if (ct.IsCancellationRequested)
      {
        throw;
      }
    }
    var output = await stdout + await stderr; return new(timed ? -1 : p.ExitCode, output, timed);
  }
  private sealed record Proc(int ExitCode, string Output, bool TimedOut);


  internal static async Task<ProjectEvaluation> GetCompileFiles(string project, RunMetrics metrics, CancellationToken ct, string? targetFramework = null,
      ArtifactCache? cache = null, List<string>? diagnostics = null)
  {
    var cacheInputs = ProjectConfigurationInputs(project, out var unsafeImport);
    var canCache = unsafeImport is null;
    var environmentFingerprint = EnvironmentFingerprint();
    var cacheKey = ArtifactCache.ContentKey(cacheInputs,
        ["project-evaluation-v4", targetFramework ?? string.Empty, environmentFingerprint, EngineVersion()]);
    var logicalIdentity = Path.GetFullPath(project) + "|" + (targetFramework ?? "default");
    if (cache is not null)
    {
      if (!canCache)
      {
        diagnostics?.Add($"CacheBypass: project evaluation contains an MSBuild import with untracked external dependencies ({unsafeImport}).");
      }
      else
      {
        var cacheRead = await cache.TryReadAsync<CachedProjectEvaluation>("project-evaluation", logicalIdentity, cacheKey, ct);
        diagnostics?.Add(cacheRead.Diagnostic);
        if (cacheRead.Found)
        {
          return CreateProjectEvaluation(cacheRead.Value!, project);
        }
      }

    }
    var args = new List<string> { "msbuild", project,
      "-getItem:Compile,ProjectReference,None,Content,EmbeddedResource,AdditionalFiles,Analyzer,Page,Resource",
      "-getProperty:AssemblyName,LangVersion,DefineConstants,TargetFramework,TargetFrameworks,TargetPath", "-nologo" };
    if (!string.IsNullOrWhiteSpace(targetFramework))
    {
      args.Add($"-p:TargetFramework={targetFramework}");
    }

    var p = await RunProcess("dotnet", args, Path.GetDirectoryName(Path.GetFullPath(project))!, null, 60, metrics, ct);
    if (p.ExitCode != 0)
    {
      throw new InvalidOperationException("Unable to evaluate project Compile items: " + p.Output);
    }

    using var doc = JsonDocument.Parse(p.Output);
    var root = doc.RootElement;
    var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project))!;
    var compileItems = root.GetProperty("Items").GetProperty("Compile").EnumerateArray().ToArray();
    var unsafeGeneratedCompileItem = compileItems
        .Select(item => item.TryGetProperty("FullPath", out var fullPath) && !string.IsNullOrWhiteSpace(fullPath.GetString())
            ? Path.GetFullPath(fullPath.GetString()!)
            : Path.GetFullPath(Path.Combine(projectDirectory, item.GetProperty("Identity").GetString()!)))
        .FirstOrDefault(file => file.Split(Path.DirectorySeparatorChar).Contains("bin", StringComparer.OrdinalIgnoreCase) ||
            file.Split(Path.DirectorySeparatorChar).Contains("obj", StringComparer.OrdinalIgnoreCase) && !IsKnownSdkGeneratedCompileSource(file));
    var buildInputs = new[] { "None", "Content", "EmbeddedResource", "AdditionalFiles", "Analyzer", "Page", "Resource" }
        .Where(itemName => root.GetProperty("Items").TryGetProperty(itemName, out _))
        .SelectMany(itemName => root.GetProperty("Items").GetProperty(itemName).EnumerateArray())
        .Where(item => item.TryGetProperty("FullPath", out var fullPath) && File.Exists(fullPath.GetString()))
        .Select(item => Path.GetFullPath(item.GetProperty("FullPath").GetString()!))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var files = compileItems
        .Select(item => Path.GetFullPath(Path.Combine(projectDirectory, item.GetProperty("Identity").GetString()!)))
        .Where(File.Exists).Where(file => !file.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) && !file.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
    var linkedCompileItems = compileItems.Where(item => item.TryGetProperty("FullPath", out var fullPathElement) &&
            !string.IsNullOrWhiteSpace(fullPathElement.GetString()))
        .Select(item => new
        {
          File = Path.GetFullPath(item.GetProperty("FullPath").GetString()!),
          Identity = item.GetProperty("Identity").GetString()!,
          DefiningProjectPath = item.TryGetProperty("DefiningProjectFullPath", out var definingProject) ? definingProject.GetString() : null
        })
        .Where(item => File.Exists(item.File) && !IsWithinDirectory(item.File, projectDirectory))
        .Select(item => new LinkedCompileItem(item.File, item.Identity, item.DefiningProjectPath ?? Path.GetFullPath(project), Path.GetFullPath(project)))
        .OrderBy(item => item.File, StringComparer.OrdinalIgnoreCase).ToArray();
    var projectReferences = root.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
        .Where(item => !item.TryGetProperty("Metadata", out var metadata) || !metadata.TryGetProperty("ReferenceOutputAssembly", out var outputAssembly) ||
            !string.Equals(outputAssembly.GetString(), "false", StringComparison.OrdinalIgnoreCase))
        .Select(item => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, item.GetProperty("Identity").GetString()!)))
        .Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var properties = root.GetProperty("Properties");
    var language = properties.GetProperty("LangVersion").GetString();
    var defineConstants = properties.GetProperty("DefineConstants").GetString();
    var evaluatedTargetFramework = properties.GetProperty("TargetFramework").GetString();
    var targetFrameworks = properties.GetProperty("TargetFrameworks").GetString();
    var assemblyName = properties.GetProperty("AssemblyName").GetString();
    var targetPath = properties.GetProperty("TargetPath").GetString();
    if (string.IsNullOrWhiteSpace(targetFramework) && string.IsNullOrWhiteSpace(evaluatedTargetFramework))
    {
      var available = (targetFrameworks ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      if (available.Length > 1)
      {
        throw new InvalidOperationException($"Project '{project}' targets multiple frameworks. Select one with --framework ({string.Join(", ", available)}).");
      }

      if (available.Length == 1)
      {
        evaluatedTargetFramework = available[0];
      }
    }
    var version = LanguageVersion.Preview;
    if (!string.IsNullOrWhiteSpace(language) && !LanguageVersionFacts.TryParse(language, out version))
    {
      throw new InvalidOperationException($"MSBuild returned an unsupported C# language version '{language}'.");
    }

    var symbols = (defineConstants ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var resolvedAssemblyName = string.IsNullOrWhiteSpace(assemblyName) ? Path.GetFileNameWithoutExtension(project) : assemblyName;
    var parseOptions = new CSharpParseOptions(version, preprocessorSymbols: symbols);
    var result = new ProjectEvaluation(files, parseOptions, evaluatedTargetFramework,
        resolvedAssemblyName, projectReferences, [Path.GetFullPath(project)], files.ToDictionary(file => file, _ => resolvedAssemblyName, StringComparer.OrdinalIgnoreCase))
    {
      TargetPath = targetPath,
      LinkedCompileItems = linkedCompileItems,
      BuildInputs = buildInputs,
      HasUnsafeGeneratedCompileInputs = unsafeGeneratedCompileItem is not null,
      ParseOptionsByFile = files.ToDictionary(file => file, _ => parseOptions, StringComparer.OrdinalIgnoreCase)
    };
    if (cache is not null && canCache)
    {
      await cache.WriteAsync("project-evaluation", logicalIdentity, cacheKey,
          new CachedProjectEvaluation(result.CompileFiles, version.ToString(), symbols, result.TargetFramework, result.AssemblyName, result.ProjectReferences,
              result.LinkedCompileItems)
          { BuildInputs = result.BuildInputs, TargetPath = result.TargetPath, HasUnsafeGeneratedCompileInputs = result.HasUnsafeGeneratedCompileInputs }, ct);
    }
    return result;
  }

  private static ArtifactCache CreateCache(MutationOptions options, string project) =>
      new(options.CacheDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, ".mutagen-cache"));

  private static string ResolveEvaluatedTargetPath(ProjectEvaluation evaluation, string project, string? targetFramework)
  {
    if (!string.IsNullOrWhiteSpace(evaluation.TargetPath))
    {
      return Path.GetFullPath(evaluation.TargetPath);
    }

    if (string.IsNullOrWhiteSpace(targetFramework))
    {
      throw new InvalidOperationException($"MSBuild did not provide a target path or framework for '{project}'.");
    }

    return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, "bin", "Debug", targetFramework,
        evaluation.AssemblyName + ".dll");
  }

  private static async Task<FileStream> AcquireCompilationOutputLockAsync(string outputDirectory, int timeoutSeconds, CancellationToken cancellationToken)
  {
    var lockDirectory = Path.Combine(Path.GetTempPath(), "mutation-framework", "compilation-locks");
    Directory.CreateDirectory(lockDirectory);
    var lockPath = Path.Combine(lockDirectory, Hash(Path.GetFullPath(outputDirectory).ToUpperInvariant()) + ".lock");
    var timer = Stopwatch.StartNew();
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      }
      catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
      {
        await Task.Delay(50, cancellationToken);
      }

      if (timer.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
      {
        throw new TimeoutException($"Timed out waiting to access compilation output '{outputDirectory}'.");
      }
    }
  }

  private static async Task<IDisposable> AcquireCompilationLocksAsync(IEnumerable<string> identities, int timeoutSeconds,
      CancellationToken cancellationToken)
  {
    var locks = new List<FileStream>();
    try
    {
      foreach (var identity in identities.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                   .Order(StringComparer.OrdinalIgnoreCase))
      {
        locks.Add(await AcquireCompilationOutputLockAsync(identity, timeoutSeconds, cancellationToken));
      }

      return new CompilationLockSet(locks);
    }
    catch
    {
      for (var index = locks.Count - 1; index >= 0; index--)
      {
        locks[index].Dispose();
      }

      throw;
    }
  }

  private static string GetCompilationArtifactKey(string project, ProjectEvaluation evaluation, string? targetFramework,
      out bool canCache, out string? bypassReason)
  {
    canCache = !evaluation.HasUnsafeGeneratedCompileInputs;
    bypassReason = evaluation.HasUnsafeGeneratedCompileInputs ? "project graph contains generated Compile items outside the cacheable SDK set" : null;
    var inputs = new HashSet<string>(evaluation.CompileFiles, StringComparer.OrdinalIgnoreCase);
    inputs.UnionWith(evaluation.BuildInputs);
    foreach (var evaluatedProject in evaluation.ProjectPaths)
    {
      var configurationInputs = ProjectConfigurationInputs(evaluatedProject, out var unresolvedImport);
      inputs.UnionWith(configurationInputs);
      inputs.UnionWith(ProjectBuildContentInputs(evaluatedProject));
      inputs.UnionWith(GeneratedCompileInputs(evaluatedProject, out var unsafeGeneratedSource));
      if (unsafeGeneratedSource is not null)
      {
        canCache = false;
        bypassReason ??= unsafeGeneratedSource;
      }
      var unsafeCodeGenerationTarget = FindUnsafeCodeGenerationTarget(configurationInputs);
      if (unsafeCodeGenerationTarget is not null)
      {
        canCache = false;
        bypassReason ??= $"custom target may generate C# from untracked inputs ({unsafeCodeGenerationTarget})";
      }
      if (unresolvedImport is not null)
      {
        canCache = false;
        bypassReason ??= $"unresolved project import {unresolvedImport}";
      }

      var assetsFile = Path.Combine(Path.GetDirectoryName(evaluatedProject)!, "obj", "project.assets.json");
      if (!File.Exists(assetsFile))
      {
        canCache = false;
        bypassReason ??= $"restore assets are missing for {evaluatedProject}";
        continue;
      }

      try
      {
        var packageInputs = PackageAssetInputs(assetsFile);
        inputs.UnionWith(packageInputs);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
      {
        canCache = false;
        bypassReason ??= $"restore assets cannot be inspected for {evaluatedProject}: {exception.Message}";
      }
    }

    var values = new List<string>
    {
      "compilation-artifacts-v1", Path.GetFullPath(project), evaluation.AssemblyName, targetFramework ?? string.Empty,
      "Configuration=Debug", EnvironmentFingerprint(), EngineVersion(),
      string.Join(";", evaluation.ProjectPaths.Order(StringComparer.OrdinalIgnoreCase)),
      string.Join(";", evaluation.ParseOptionsByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
          .Select(pair => pair.Key + "=" + pair.Value.LanguageVersion + ":" + string.Join(",", pair.Value.PreprocessorSymbolNames.Order(StringComparer.Ordinal))))
    };
    return ArtifactCache.ContentKey(inputs, values);
  }

  private static string GetMutationCompilationArtifactKey(string project, ProjectEvaluation evaluation, string? targetFramework,
      IReadOnlyList<Candidate> candidates, MutationOptions options, out bool canCache, out string? bypassReason)
  {
    var projectKey = GetCompilationArtifactKey(project, evaluation, targetFramework, out canCache, out bypassReason);
    if (!canCache)
    {
      return projectKey;
    }

    var mutatorFingerprint = options.Registry?.Fingerprint ?? string.Join(";",
        (options.Mutators ?? MutatorRegistry.CreateDefault().Mutators.Select(mutator => mutator.Id)
            .ToHashSet(StringComparer.Ordinal)).Order(StringComparer.Ordinal));
    var candidateFingerprint = candidates.OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
        .Select(candidate => string.Join("|", candidate.Id, candidate.SiteId, candidate.Operator, candidate.File,
            candidate.Start, candidate.Length, candidate.Original, candidate.Replacement, candidate.ReplacementToken,
            candidate.SiteRuntimeId, candidate.MutantRuntimeId, candidate.ApplicationKind));
    return ArtifactCache.ContentKey([], ["mutation-compilation-artifacts-v1", projectKey, mutatorFingerprint,
        options.Include ?? string.Empty, options.Exclude ?? string.Empty,
        string.Join("\n", candidateFingerprint), RuntimeSource(candidates.Select(candidate => candidate.SiteRuntimeId)
            .Distinct(StringComparer.Ordinal).Count()), typeof(Engine).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
        typeof(TestPlatformSession).Assembly.ManifestModule.ModuleVersionId.ToString("N")]);
  }

  private static List<string> GeneratedCompileInputs(string project, out string? unsafeReason)
  {
    unsafeReason = null;
    var intermediateDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, "obj");
    if (!Directory.Exists(intermediateDirectory))
    {
      return [];
    }

    var inputs = new List<string>();
    foreach (var source in Directory.EnumerateFiles(intermediateDirectory, "*.cs", SearchOption.AllDirectories))
    {
      if (!IsKnownSdkGeneratedCompileSource(source))
      {
        unsafeReason ??= $"unrecognized generated C# compile input '{source}'";
        continue;
      }

      inputs.Add(Path.GetFullPath(source));
    }

    return inputs;
  }

  private static bool IsKnownSdkGeneratedCompileSource(string path)
  {
    var name = Path.GetFileName(path);
    return name.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);
  }

  private static string? FindUnsafeCodeGenerationTarget(IEnumerable<string> configurationInputs)
  {
    foreach (var path in configurationInputs.Where(path => new[] { ".csproj", ".props", ".targets" }
                 .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
    {
      if (!File.Exists(path) || IsBuiltInTestSdkTarget(path) || IsBuiltInTestPlatformTarget(path) || IsDotNetSdkFile(path))
      {
        continue;
      }

      XDocument document;
      try { document = XDocument.Load(path); }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
      {
        return path;
      }

      var generatesCompileInputs = document.Descendants().Where(element => element.Name.LocalName == "Target")
          .SelectMany(target => target.Descendants())
          .Any(element => element.Name.LocalName == "Exec" || element.Name.LocalName == "Compile" &&
              (element.Attribute("Include") is not null || element.Attribute("Update") is not null));
      if (generatesCompileInputs)
      {
        return path;
      }
    }

    return null;
  }

  private static bool IsBuiltInTestSdkTarget(string path) =>
      Path.GetFileName(path).Equals("Microsoft.NET.Test.Sdk.targets", StringComparison.OrdinalIgnoreCase) &&
      path.Split(Path.DirectorySeparatorChar).Contains("microsoft.net.test.sdk", StringComparer.OrdinalIgnoreCase);

  private static bool IsBuiltInTestPlatformTarget(string path) =>
      Path.GetFileName(path).Equals("Microsoft.Testing.Platform.MSBuild.targets", StringComparison.OrdinalIgnoreCase) &&
      path.Split(Path.DirectorySeparatorChar).Contains("microsoft.testing.platform.msbuild", StringComparer.OrdinalIgnoreCase);

  private static bool IsDotNetSdkFile(string path)
  {
    var fullPath = Path.GetFullPath(path);
    var dotnetRoots = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.GetFullPath(directory.Trim('"')))
        .Where(directory => File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")));
    return dotnetRoots.Any(root => IsWithinDirectory(fullPath, Path.Combine(root, "sdk")));
  }

  private static Dictionary<string, string> SnapshotCompilationSources(string root)
  {
    return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("bin", StringComparer.OrdinalIgnoreCase))
        .ToDictionary(path => Path.GetRelativePath(root, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
  }

  private static string? FindUnsafeGeneratedCompileChange(string root, IReadOnlyDictionary<string, string> before)
  {
    var after = SnapshotCompilationSources(root);
    foreach (var relativePath in before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                 .Order(StringComparer.OrdinalIgnoreCase))
    {
      var hadBefore = before.TryGetValue(relativePath, out var beforeHash);
      var hasAfter = after.TryGetValue(relativePath, out var afterHash);
      if (hadBefore == hasAfter && string.Equals(beforeHash, afterHash, StringComparison.Ordinal))
      {
        continue;
      }

      var fullPath = Path.Combine(root, relativePath);
      var isSdkGenerated = relativePath.Split(Path.DirectorySeparatorChar).Contains("obj", StringComparer.OrdinalIgnoreCase) &&
          IsKnownSdkGeneratedCompileSource(fullPath);
      if (!isSdkGenerated)
      {
        return fullPath;
      }
    }

    return null;
  }

  private static HashSet<string> ProjectBuildContentInputs(string project)
  {
    var root = Path.GetDirectoryName(Path.GetFullPath(project))!;
    var excluded = new HashSet<string>(["bin", "obj", ".git", ".mutagen-cache", "artifacts", "node_modules", ".vs", "TestResults"], StringComparer.OrdinalIgnoreCase);
    var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.TryPop(out var directory))
    {
      foreach (var file in Directory.EnumerateFiles(directory))
      {
        inputs.Add(Path.GetFullPath(file));
      }

      foreach (var child in Directory.EnumerateDirectories(directory))
      {
        if (!excluded.Contains(Path.GetFileName(child)))
        {
          pending.Push(child);
        }
      }
    }

    return inputs;
  }

  private static HashSet<string> PackageAssetInputs(string assetsFile)
  {
    using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
    var root = document.RootElement;
    var packageFolders = root.GetProperty("packageFolders").EnumerateObject().Select(property => Path.GetFullPath(property.Name)).ToArray();
    var libraries = root.GetProperty("libraries");
    var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!root.TryGetProperty("targets", out var targets))
    {
      return inputs;
    }

    foreach (var target in targets.EnumerateObject())
    {
      foreach (var library in target.Value.EnumerateObject())
      {
        if (!libraries.TryGetProperty(library.Name, out var libraryMetadata) ||
            !libraryMetadata.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase) ||
            !libraryMetadata.TryGetProperty("path", out var packagePathElement))
        {
          continue;
        }

        var packagePath = packagePathElement.GetString();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
          continue;
        }

        foreach (var assetGroup in library.Value.EnumerateObject())
        {
          foreach (var assetPath in EnumerateAssetPaths(assetGroup.Value))
          {
            if (assetPath.EndsWith("/_._", StringComparison.Ordinal) || assetPath == "_._")
            {
              continue;
            }

            foreach (var packageFolder in packageFolders)
            {
              var fullPath = Path.GetFullPath(Path.Combine(packageFolder, packagePath, assetPath.Replace('/', Path.DirectorySeparatorChar)));
              if (File.Exists(fullPath))
              {
                inputs.Add(fullPath);
              }
            }
          }
        }
      }
    }

    return inputs;

    static IEnumerable<string> EnumerateAssetPaths(JsonElement element)
    {
      if (element.ValueKind == JsonValueKind.Object)
      {
        foreach (var property in element.EnumerateObject())
        {
          if (property.Name.Contains('/') && Path.HasExtension(property.Name))
          {
            yield return property.Name;
          }
          else
          {
            foreach (var nested in EnumerateAssetPaths(property.Value))
            {
              yield return nested;
            }
          }
        }
      }
      else if (element.ValueKind == JsonValueKind.Array)
      {
        foreach (var item in element.EnumerateArray())
        {
          foreach (var nested in EnumerateAssetPaths(item))
          {
            yield return nested;
          }
        }
      }
    }
  }

  private static async Task<(bool Restored, string Diagnostic)> TryRestoreCompilationArtifacts(ArtifactCache cache, string phase, string logicalIdentity,
      string contentKey, string outputDirectory, bool privateOutputDirectory, CancellationToken cancellationToken)
  {
    if (!privateOutputDirectory)
    {
      return (false, $"CacheBypass: {phase} output restore skipped because '{outputDirectory}' is shared with external build tools.");
    }

    var cacheRead = await cache.TryReadAsync<CachedCompilationArtifacts>(phase, logicalIdentity, contentKey,
        cancellationToken);
    if (!cacheRead.Found)
    {
      return (false, cacheRead.Diagnostic);
    }

    var artifact = cacheRead.Value;

    if (artifact?.Files is null || artifact.Files.Length == 0)
    {
      return (false, $"CacheInvalidated: {phase} {contentKey} has an empty output manifest");
    }

    var outputRoot = Path.GetFullPath(outputDirectory);
    foreach (var file in artifact.Files)
    {
      if (string.IsNullOrWhiteSpace(file.RelativePath))
      {
        return (false, $"CacheInvalidated: {phase} manifest contains an empty output path");
      }

      var destination = Path.GetFullPath(Path.Combine(outputRoot, file.RelativePath));
      var relative = Path.GetRelativePath(outputRoot, destination);
      if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
      {
        return (false, $"CacheInvalidated: {phase} manifest contains an unsafe path '{file.RelativePath}'");
      }
    }

    if (!string.Equals(artifact.IntegrityHash, CompilationArtifactsIntegrity(artifact.Files), StringComparison.Ordinal))
    {
      return (false, $"CacheInvalidated: {phase} output manifest integrity check failed");
    }

    var stagingRoot = outputRoot + ".mutagen-restore-" + Guid.NewGuid().ToString("N");
    try
    {
      foreach (var file in artifact.Files)
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(file.RelativePath))
        {
          return (false, $"CacheInvalidated: {phase} manifest contains an empty output path");
        }

        var destination = Path.GetFullPath(Path.Combine(stagingRoot, file.RelativePath));
        var relative = Path.GetRelativePath(stagingRoot, destination);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
          return (false, $"CacheInvalidated: {phase} manifest contains an unsafe path '{file.RelativePath}'");
        }

        if (!await cache.TryCopyBlobAsync(phase, file.BlobKey, destination, cancellationToken))
        {
          return (false, $"CacheInvalidated: {phase} blob '{file.BlobKey}' is missing or corrupt");
        }
      }

      cancellationToken.ThrowIfCancellationRequested();
      if (Directory.Exists(outputRoot))
      {
        Directory.Delete(outputRoot, true);
      }

      Directory.CreateDirectory(Path.GetDirectoryName(outputRoot)!);
      Directory.Move(stagingRoot, outputRoot);
      return (true, $"CacheHit: {phase} {contentKey}");
    }
    finally
    {
      if (Directory.Exists(stagingRoot))
      {
        Directory.Delete(stagingRoot, true);
      }
    }
  }

  private static async Task StoreCompilationArtifacts(ArtifactCache cache, string phase, string logicalIdentity, string contentKey, string outputDirectory,
      CancellationToken cancellationToken)
  {
    if (!Directory.Exists(outputDirectory))
    {
      return;
    }

    var outputRoot = Path.GetFullPath(outputDirectory);
    var files = new List<CachedCompilationFile>();
    foreach (var path in Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (phase.Equals("mutation-compilation-artifacts", StringComparison.Ordinal) &&
          Path.GetExtension(path).Equals(".pdb", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      files.Add(new(Path.GetRelativePath(outputRoot, path), await cache.StoreBlobAsync(phase, path, cancellationToken)));
    }

    var artifacts = files.ToArray();
    await cache.WriteAsync(phase, logicalIdentity, contentKey,
        new CachedCompilationArtifacts(artifacts, CompilationArtifactsIntegrity(artifacts)), cancellationToken);
  }

  private static string CompilationArtifactsIntegrity(IEnumerable<CachedCompilationFile> files) =>
      ArtifactCache.ContentKey([], ["compilation-artifact-manifest-v1", .. files
          .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
          .Select(file => file.RelativePath + "=" + file.BlobKey)]);

  private sealed class CompilationLockSet(IReadOnlyList<FileStream> locks) : IDisposable
  {
    public void Dispose()
    {
      for (var index = locks.Count - 1; index >= 0; index--)
      {
        locks[index].Dispose();
      }
    }
  }

  private static bool IsStableTestDiscoveryInput(string path)
  {
    var name = Path.GetFileName(path);
    return !name.StartsWith(".msCoverageSourceRootsMapping", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("nunit_random_seed.tmp", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(".stryker-unchanged", StringComparison.OrdinalIgnoreCase);
  }

  private static ProjectEvaluation CreateProjectEvaluation(CachedProjectEvaluation cached, string project)
  {
    if (!Enum.TryParse<LanguageVersion>(cached.LanguageVersion, true, out var version) || !Enum.IsDefined(version))
    {
      throw new InvalidDataException($"Cached project evaluation contains unknown language version '{cached.LanguageVersion}'.");
    }
    var parseOptions = new CSharpParseOptions(version, preprocessorSymbols: cached.DefineConstants);
    return new ProjectEvaluation(cached.CompileFiles, parseOptions, cached.TargetFramework,
        cached.AssemblyName, cached.ProjectReferences, [Path.GetFullPath(project)], cached.CompileFiles.ToDictionary(file => file, _ => cached.AssemblyName, StringComparer.OrdinalIgnoreCase))
    {
      TargetPath = cached.TargetPath,
      LinkedCompileItems = cached.LinkedCompileItems,
      BuildInputs = cached.BuildInputs,
      HasUnsafeGeneratedCompileInputs = cached.HasUnsafeGeneratedCompileInputs,
      ParseOptionsByFile = cached.CompileFiles.ToDictionary(file => file, _ => parseOptions, StringComparer.OrdinalIgnoreCase)
    };
  }

  internal static async Task<ProjectEvaluation> GetProjectGraph(string project, RunMetrics metrics, CancellationToken cancellationToken,
      string? requestedFramework, ArtifactCache cache, List<string> diagnostics)
  {
    var rootFramework = await SelectProjectFramework(project, requestedFramework, requestedFramework, true, metrics, cancellationToken, cache, diagnostics);
    var root = await GetCompileFiles(project, metrics, cancellationToken, rootFramework, cache, diagnostics);
    var files = new HashSet<string>(root.CompileFiles, StringComparer.OrdinalIgnoreCase);
    var linkedCompileItems = new List<LinkedCompileItem>(root.LinkedCompileItems);
    var buildInputs = new HashSet<string>(root.BuildInputs, StringComparer.OrdinalIgnoreCase);
    var hasUnsafeGeneratedCompileInputs = root.HasUnsafeGeneratedCompileInputs;
    var assemblyNames = new Dictionary<string, string>(root.AssemblyNamesByFile, StringComparer.OrdinalIgnoreCase);
    var parseOptionsByFile = new Dictionary<string, CSharpParseOptions>(root.ParseOptionsByFile, StringComparer.OrdinalIgnoreCase);
    var projects = new HashSet<string>([Path.GetFullPath(project)], StringComparer.OrdinalIgnoreCase);
    var pending = new Queue<string>(root.ProjectReferences);
    while (pending.TryDequeue(out var reference))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var fullReference = Path.GetFullPath(reference);
      if (!projects.Add(fullReference))
      {
        continue;
      }

      var framework = await SelectProjectFramework(fullReference, null, root.TargetFramework, false, metrics, cancellationToken, cache, diagnostics);
      var dependency = await GetCompileFiles(fullReference, metrics, cancellationToken, framework, cache, diagnostics);
      linkedCompileItems.AddRange(dependency.LinkedCompileItems);
      buildInputs.UnionWith(dependency.BuildInputs);
      hasUnsafeGeneratedCompileInputs |= dependency.HasUnsafeGeneratedCompileInputs;
      foreach (var file in dependency.CompileFiles)
      {
        files.Add(file);
        assemblyNames[file] = dependency.AssemblyName;
        parseOptionsByFile[file] = dependency.ParseOptionsByFile[file];
      }
      foreach (var transitiveReference in dependency.ProjectReferences)
      {
        pending.Enqueue(transitiveReference);
      }
    }
    return root with
    {
      CompileFiles = files.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      ProjectPaths = projects.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      AssemblyNamesByFile = assemblyNames,
      ParseOptionsByFile = parseOptionsByFile,
      LinkedCompileItems = linkedCompileItems.DistinctBy(item => (item.File, item.DefiningProjectPath, item.Identity)).ToArray(),
      BuildInputs = buildInputs.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      HasUnsafeGeneratedCompileInputs = hasUnsafeGeneratedCompileInputs
    };
  }

  internal static async Task<ProjectEvaluation> GetProductionGraph(string primaryProject, IReadOnlyList<string> additionalProjects,
      RunMetrics metrics, CancellationToken cancellationToken, string? requestedFramework, ArtifactCache cache, List<string> diagnostics)
  {
    var projects = new[] { primaryProject }.Concat(additionalProjects).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var graphs = new List<ProjectEvaluation>(projects.Length);
    foreach (var project in projects)
    {
      graphs.Add(await GetProjectGraph(project, metrics, cancellationToken, requestedFramework, cache, diagnostics));
    }
    var files = graphs.SelectMany(graph => graph.CompileFiles).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var assemblyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var parseOptionsByFile = new Dictionary<string, CSharpParseOptions>(StringComparer.OrdinalIgnoreCase);
    foreach (var graph in graphs)
    {
      foreach (var mapping in graph.AssemblyNamesByFile)
      {
        assemblyNames[mapping.Key] = mapping.Value;
      }
      foreach (var mapping in graph.ParseOptionsByFile)
      {
        parseOptionsByFile[mapping.Key] = mapping.Value;
      }
    }
    return graphs[0] with
    {
      CompileFiles = files,
      ProjectPaths = graphs.SelectMany(graph => graph.ProjectPaths).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      AssemblyNamesByFile = assemblyNames,
      ParseOptionsByFile = parseOptionsByFile,
      LinkedCompileItems = graphs.SelectMany(graph => graph.LinkedCompileItems)
          .DistinctBy(item => (item.File, item.DefiningProjectPath, item.Identity)).ToArray(),
      BuildInputs = graphs.SelectMany(graph => graph.BuildInputs).Distinct(StringComparer.OrdinalIgnoreCase)
          .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      HasUnsafeGeneratedCompileInputs = graphs.Any(graph => graph.HasUnsafeGeneratedCompileInputs)
    };
  }

  private static async Task<string?> SelectProjectFramework(string project, string? requestedFramework, string? rootFramework,
      bool isRoot, RunMetrics metrics, CancellationToken cancellationToken, ArtifactCache? cache = null, List<string>? diagnostics = null)
  {
    var projectPath = Path.GetFullPath(project);
    var inputs = ProjectConfigurationInputs(projectPath, out var unresolvedImport);
    var cacheable = cache is not null && unresolvedImport is null;
    var key = ArtifactCache.ContentKey(inputs,
        ["project-framework-selection-v1", projectPath, EnvironmentFingerprint(), EngineVersion()]);
    CachedTargetFrameworkProperties? evaluated = null;
    var cacheHit = false;
    if (cacheable)
    {
      var cacheRead = await cache!.TryReadAsync<CachedTargetFrameworkProperties>("project-framework-selection", projectPath,
          key, cancellationToken);
      diagnostics?.Add(cacheRead.Diagnostic);
      evaluated = cacheRead.Value;
      cacheHit = cacheRead.Found;
    }
    else if (cache is not null)
    {
      diagnostics?.Add($"CacheBypass: project framework selection has an unresolved import ({unresolvedImport}).");
    }

    if (!cacheHit)
    {
      var args = new[] { "msbuild", projectPath, "-getProperty:TargetFramework,TargetFrameworks", "-nologo" };
      var process = await RunProcess("dotnet", args, Path.GetDirectoryName(projectPath)!, null, 60, metrics, cancellationToken);
      if (process.ExitCode != 0)
      {
        throw new InvalidOperationException($"Unable to evaluate target frameworks for '{project}'. {process.Output}");
      }

      using var document = JsonDocument.Parse(process.Output);
      var properties = document.RootElement.GetProperty("Properties");
      evaluated = new CachedTargetFrameworkProperties(properties.GetProperty("TargetFramework").GetString(),
          properties.GetProperty("TargetFrameworks").GetString());
      if (cacheable)
      {
        await cache!.WriteAsync("project-framework-selection", projectPath, key, evaluated, cancellationToken);
      }
    }

    var single = evaluated!.TargetFramework;
    var frameworks = (evaluated.TargetFrameworks ?? string.Empty)
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (frameworks.Length > 1)
    {
      var selected = requestedFramework ?? rootFramework;
      if (string.IsNullOrWhiteSpace(selected))
      {
        throw new InvalidOperationException($"Project '{project}' targets multiple frameworks. Select one with --framework ({string.Join(", ", frameworks)}).");
      }
      if (!frameworks.Contains(selected, StringComparer.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException($"Selected framework '{selected}' is not supported by project '{project}' ({string.Join(", ", frameworks)}).");
      }
      return selected;
    }
    if (isRoot && !string.IsNullOrWhiteSpace(requestedFramework) && !string.IsNullOrWhiteSpace(single) &&
        !string.Equals(requestedFramework, single, StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException($"Selected framework '{requestedFramework}' does not match project '{project}' target '{single}'.");
    }
    return null;
  }

  internal static async Task<List<Candidate>> DiscoverCandidatesCached(string[] files, string project, MutationOptions options, List<string> diagnostics,
      CancellationToken cancellationToken, List<MutationResult> unsupportedMutations, ProjectEvaluation evaluation, ArtifactCache cache)
  {
    var projectIdentity = Path.GetFullPath(project) + "|" + evaluation.TargetFramework;
    var configurationInputs = evaluation.ProjectPaths.SelectMany(path => ProjectConfigurationInputs(path, out _)).Distinct(StringComparer.OrdinalIgnoreCase);
    var unresolvedImport = evaluation.ProjectPaths.Select(path =>
    {
      _ = ProjectConfigurationInputs(path, out var unresolved);
      return unresolved;
    }).FirstOrDefault(value => value is not null);
    var cacheInputs = files.Concat(configurationInputs).Distinct(StringComparer.OrdinalIgnoreCase);
    var values = new List<string>
    {
      "mutation-discovery-v1", projectIdentity, evaluation.AssemblyName, evaluation.ParseOptions.LanguageVersion.ToString(),
      string.Join(";", evaluation.ParseOptions.PreprocessorSymbolNames.Order(StringComparer.Ordinal)),
      string.Join(";", evaluation.AssemblyNamesByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)),
      string.Join(";", evaluation.ParseOptionsByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
          .Select(pair => pair.Key + "=" + pair.Value.LanguageVersion + ":" + string.Join(",", pair.Value.PreprocessorSymbolNames.Order(StringComparer.Ordinal)))),
      options.Registry?.Fingerprint ?? string.Join(";", (options.Mutators ?? MutatorRegistry.CreateDefault().Mutators.Select(mutator => mutator.Id).ToHashSet(StringComparer.Ordinal)).Order(StringComparer.Ordinal)),
      options.Include ?? string.Empty, options.Exclude ?? string.Empty, EnvironmentFingerprint(),
      EngineVersion()
    };
    var contentKey = ArtifactCache.ContentKey(cacheInputs, values);
    if (unresolvedImport is not null)
    {
      diagnostics.Add($"CacheBypass: mutation discovery found an MSBuild import with an unresolved property path ({unresolvedImport}).");
    }
    else
    {
      var cacheRead = await cache.TryReadAsync<CandidateDiscoveryArtifact>("mutation-discovery", projectIdentity,
          contentKey, cancellationToken);
      if (cacheRead.Found)
      {
        diagnostics.Add(cacheRead.Diagnostic);
        var artifact = cacheRead.Value!;
        diagnostics.AddRange(artifact!.Diagnostics);
        unsupportedMutations.AddRange(artifact.UnsupportedMutations);
        return artifact.Candidates.ToList();
      }
      diagnostics.Add(cacheRead.Diagnostic);
    }
    var localDiagnostics = new List<string>();
    var localUnsupported = new List<MutationResult>();
    var candidates = DiscoverCandidates(files, project, options, localDiagnostics, cancellationToken, localUnsupported,
        evaluation.ParseOptions, evaluation.TargetFramework, evaluation.AssemblyName, evaluation.AssemblyNamesByFile, evaluation.ParseOptionsByFile);
    diagnostics.AddRange(localDiagnostics);
    unsupportedMutations.AddRange(localUnsupported);
    if (unresolvedImport is null)
    {
      await cache.WriteAsync("mutation-discovery", projectIdentity, contentKey,
          new CandidateDiscoveryArtifact(candidates.ToArray(), localUnsupported.ToArray(), localDiagnostics.ToArray()), cancellationToken);
    }
    return candidates;
  }

  private static string[] ProjectConfigurationInputs(string project, out string? unresolvedImport)
  {
    var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project))!;
    var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(project) };
    unresolvedImport = null;
    var pending = new Stack<string>();
    pending.Push(projectDirectory);
    while (pending.TryPop(out var directory))
    {
      foreach (var child in Directory.EnumerateDirectories(directory))
      {
        if (!new[] { "bin", "obj", ".git", ".mutagen-cache", "artifacts", "node_modules" }
            .Any(name => Path.GetFileName(child).Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
          pending.Push(child);
        }
      }
      foreach (var path in Directory.EnumerateFiles(directory))
      {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) || extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
          inputs.Add(Path.GetFullPath(path));
        }
      }
    }
    var projectName = Path.GetFileName(project);
    foreach (var path in new[]
    {
      Path.Combine(projectDirectory, "obj", "project.assets.json"),
      Path.Combine(projectDirectory, "obj", projectName + ".nuget.g.props"),
      Path.Combine(projectDirectory, "obj", projectName + ".nuget.g.targets")
    })
    {
      inputs.Add(path);
    }
    for (var directory = new DirectoryInfo(projectDirectory); directory is not null; directory = directory.Parent)
    {
      foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config" })
      {
        var path = Path.Combine(directory.FullName, name);
        if (File.Exists(path))
        {
          inputs.Add(path);
        }
      }
    }

    var importedFiles = new Stack<string>(inputs.Where(path => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".props", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".targets", StringComparison.OrdinalIgnoreCase)));
    var inspectedImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var inspectedPackageSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var nugetPackageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (string.IsNullOrWhiteSpace(nugetPackageRoot))
    {
      nugetPackageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }
    nugetPackageRoot = Path.GetFullPath(nugetPackageRoot) + Path.DirectorySeparatorChar;
    while (importedFiles.TryPop(out var importingFile))
    {
      if (!inspectedImports.Add(importingFile) || !File.Exists(importingFile))
      {
        continue;
      }

      XDocument document;
      try { document = XDocument.Load(importingFile); }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
      {
        unresolvedImport ??= $"cannot inspect imported file '{importingFile}': {exception.Message}";
        continue;
      }

      var packageRelativePath = Path.GetRelativePath(nugetPackageRoot, importingFile);
      var packageSegments = packageRelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
          StringSplitOptions.RemoveEmptyEntries);
      if (!Path.IsPathRooted(packageRelativePath) && packageSegments.Length >= 2 &&
          !packageSegments[0].Equals("..", StringComparison.Ordinal))
      {
        var packageDirectory = Path.Combine(nugetPackageRoot, packageSegments[0], packageSegments[1]);
        if (Directory.Exists(packageDirectory) && inspectedPackageSources.Add(packageDirectory))
        {
          inputs.UnionWith(Directory.EnumerateFiles(packageDirectory, "*.cs", SearchOption.AllDirectories)
              .Select(Path.GetFullPath));
        }
      }

      foreach (var import in document.Descendants().Where(element => element.Name.LocalName == "Import"))
      {
        var importPath = import.Attribute("Project")?.Value;
        if (string.IsNullOrWhiteSpace(importPath))
        {
          continue;
        }

        importPath = importPath.Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(importingFile) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileFullPath)", importingFile, StringComparison.OrdinalIgnoreCase)
            .Replace("$(NuGetPackageRoot)", nugetPackageRoot, StringComparison.OrdinalIgnoreCase);
        importPath = Regex.Replace(importPath,
            @"\$\(\[System\.IO\.Path\]::GetFileName\(\$\(\[System\.IO\.Path\]::GetDirectoryName\((?<path>[^()]*)\)\)\)\)",
            match => Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(match.Groups["path"].Value)) ?? string.Empty),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (importPath.IndexOfAny(['*', '?']) >= 0)
        {
          unresolvedImport ??= $"'{importingFile}' imports wildcard path '{importPath}', whose resolved file set is not tracked safely";
          continue;
        }
        if (importPath.Contains("$(", StringComparison.Ordinal))
        {
          unresolvedImport ??= $"'{importingFile}' imports unresolved path '{importPath}'";
          continue;
        }
        var resolved = Path.GetFullPath(Path.IsPathRooted(importPath) ? importPath : Path.Combine(Path.GetDirectoryName(importingFile)!, importPath));
        if (File.Exists(resolved) && inputs.Add(resolved))
        {
          importedFiles.Push(resolved);
        }
      }
    }
    return inputs.Order(StringComparer.OrdinalIgnoreCase).ToArray();
  }

  internal static string EnvironmentFingerprint()
      => string.Join("\n", EnvironmentFingerprintParts());

  private static string[] EnvironmentFingerprintParts()
  {
    // PATH contains a per-invocation Codex command shim in this environment. Hashing it verbatim
    // prevents cross-run reuse even though the selected .NET SDK is unchanged. Capture actual
    // dotnet roots and their installed SDK versions instead; keep all other environment inputs.
    var volatileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      "PATH", "TEMP", "TMP", "TMPDIR", "NUMBER_OF_PROCESSORS", "SESSIONNAME", "ChocolateyLastPathUpdate"
    };
    var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .Where(entry => !volatileNames.Contains((string)entry.Key) && !((string)entry.Key).StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase))
        .Select(entry => $"{entry.Key}={Hash(Convert.ToString(entry.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)}")
        .Order(StringComparer.Ordinal).ToList();
    var dotnetRoots = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => Path.GetFullPath(path.Trim('"')))
        .Where(path => File.Exists(Path.Combine(path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    values.Add($"DOTNET_PATH_ORDER={Hash(string.Join("|", dotnetRoots.Select((root, index) => $"{index}:{root}")))}");
    foreach (var root in dotnetRoots)
    {
      var sdkDirectory = Path.Combine(root, "sdk");
      IEnumerable<string?> versions = Directory.Exists(sdkDirectory)
          ? Directory.GetDirectories(sdkDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal)
          : Array.Empty<string?>();
      values.Add($"DOTNET_SDK_ROOT={Hash(root + ":" + string.Join(",", versions))}");
    }

    values.Add($"RUNTIME={Hash(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ":" + Environment.Version)}");
    return values.Order(StringComparer.Ordinal).ToArray();
  }

  private static string EngineVersion() => typeof(MutationEngine).Assembly.ManifestModule.ModuleVersionId.ToString("N") + ":" +
      typeof(MutatorRegistry).Assembly.ManifestModule.ModuleVersionId.ToString("N");

  private static string FindTestAssembly(string? evaluatedTargetPath, string originalProject, string assemblyName,
      string? targetFramework = null, string? clonedProject = null)
  {
    string? assembly = null;
    if (!string.IsNullOrWhiteSpace(evaluatedTargetPath))
    {
      var fullTargetPath = Path.GetFullPath(evaluatedTargetPath);
      if (clonedProject is null)
      {
        assembly = fullTargetPath;
      }
      else
      {
        var relativeTargetPath = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(originalProject))!, fullTargetPath);
        assembly = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clonedProject))!, relativeTargetPath));
      }
    }

    var framework = targetFramework;
    if (string.IsNullOrWhiteSpace(assembly) || !File.Exists(assembly))
    {
      if (string.IsNullOrWhiteSpace(framework))
      {
        throw new InvalidOperationException($"MSBuild did not provide a usable TargetPath for test project '{originalProject}'.");
      }

      var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(clonedProject ?? originalProject))!;
      assembly = Path.Combine(projectDirectory, "bin", "Debug", framework, assemblyName + ".dll");
    }

    return File.Exists(assembly) ? assembly : throw new FileNotFoundException("Built test assembly not found at the MSBuild-evaluated target path.", assembly);
  }

  internal static List<Candidate> DiscoverCandidates(string[] files, string project, MutationOptions opt, List<string> diagnostics,
      CancellationToken ct, List<MutationResult>? unsupportedMutations = null, CSharpParseOptions? evaluatedParseOptions = null,
      string? evaluatedTargetFramework = null, string? assemblyIdentity = null, IReadOnlyDictionary<string, string>? assemblyNamesByFile = null,
      IReadOnlyDictionary<string, CSharpParseOptions>? parseOptionsByFile = null)
  {
    var list = new List<Candidate>(); var root = Path.GetDirectoryName(Path.GetFullPath(project))!;
    var parseOptions = evaluatedParseOptions ?? new CSharpParseOptions(LanguageVersion.Preview);
    var sourceByFile = files.ToDictionary(file => file, File.ReadAllText, StringComparer.OrdinalIgnoreCase);
    var trees = files
        .Select(f => (File: f, Tree: CSharpSyntaxTree.ParseText(sourceByFile[f], path: f,
            options: parseOptionsByFile?.GetValueOrDefault(f) ?? parseOptions))).ToList();
    var targetFramework = evaluatedTargetFramework ?? opt.TargetFramework ?? "net11.0";
    assemblyIdentity ??= Path.GetFileNameWithoutExtension(project);
    var globalUsings = Path.Combine(root, "obj", "Debug", targetFramework, Path.GetFileNameWithoutExtension(project) + ".GlobalUsings.g.cs");
    if (File.Exists(globalUsings))
    {
      trees.Add((globalUsings, CSharpSyntaxTree.ParseText(File.ReadAllText(globalUsings), path: globalUsings, options: parseOptions)));
    }

    var referencePaths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
    var references = referencePaths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    var compilation = CSharpCompilation.Create("MutagenSemanticValidation", trees.Select(x => x.Tree), references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    var treeByFile = trees.ToDictionary(x => x.File, x => x.Tree, StringComparer.OrdinalIgnoreCase);
    var registry = opt.Registry ?? MutatorRegistry.CreateDefault(opt.Mutators);
    foreach (var file in files)
    {
      ct.ThrowIfCancellationRequested(); var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
      var fileAssemblyIdentity = assemblyNamesByFile?.GetValueOrDefault(file) ?? assemblyIdentity;
      if (IsGeneratedSource(file, sourceByFile[file]))
      {
        continue;
      }

      if (opt.Include != null && !rel.Contains(opt.Include, StringComparison.OrdinalIgnoreCase) || opt.Exclude != null && rel.Contains(opt.Exclude, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      var tree = treeByFile[file]; var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
      foreach (var node in tree.GetRoot().DescendantNodes())
      {
        foreach (var description in registry.Generate(node, model, IsSupportedMutationSite))
        {
          var reason = description.UnsupportedReason ?? GetSchemaCompatibilityFailure(description);
          if (reason is not null)
          {
            diagnostics.Add($"Unsupported {description.Mutator.Category} mutation at {rel}:{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: {reason}");
            var unsupportedNode = description.Node;
            var unsupportedId = Hash(fileAssemblyIdentity + "|" + targetFramework + "|" + rel + "|" + unsupportedNode.SpanStart + "|" + description.ReplacementToken);
            var unsupportedSite = Hash(fileAssemblyIdentity + "|" + targetFramework + "|" + rel + "|" + unsupportedNode.SpanStart + "|" + unsupportedNode.Span.Length);
            var unsupportedPosition = tree.GetLineSpan(unsupportedNode.Span).StartLinePosition;
            unsupportedMutations?.Add(new MutationResult(unsupportedId, unsupportedSite, description.Mutator.Category.ToString(), rel,
                unsupportedPosition.Line + 1, unsupportedPosition.Character + 1, description.Original, description.Replacement,
                MutationStatus.Unsupported, [], [], Diagnostic: reason));
            continue;
          }
          var target = description.Node;
          var id = Hash(fileAssemblyIdentity + "|" + targetFramework + "|" + rel + "|" + target.SpanStart + "|" + description.ReplacementToken);
          var site = Hash(fileAssemblyIdentity + "|" + targetFramework + "|" + rel + "|" + target.SpanStart + "|" + target.Span.Length);
          var line = tree.GetLineSpan(target.Span).StartLinePosition;
          list.Add(new Candidate(id, site, description.Mutator.Category.ToString(), rel, target.SpanStart, target.Span.Length,
              line.Line + 1, line.Character + 1, description.Original, description.Replacement,
              description.ReplacementToken, "0", "0", target is ExpressionSyntax expression
                  ? (model.GetTypeInfo(expression).ConvertedType ?? model.GetTypeInfo(expression).Type)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                  : null, description.ApplicationKind)
          {
            UseInlineSiteProbe = target is ExpressionSyntax candidateExpression &&
                (HasExplicitOverflowContext(candidateExpression) || IsExpressionTreeContext(candidateExpression, model) ||
                    model.GetTypeInfo(candidateExpression).Type?.IsRefLikeType == true ||
                    model.GetTypeInfo(candidateExpression).ConvertedType?.IsRefLikeType == true ||
                    model.GetTypeInfo(candidateExpression).Type?.TypeKind == TypeKind.Pointer ||
                    model.GetTypeInfo(candidateExpression).ConvertedType?.TypeKind == TypeKind.Pointer)
          });
        }
      }
    }
    var unique = list.GroupBy(x => (x.File, x.Start, x.Length, x.ReplacementToken)).Select(x => x.First()).OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    var siteIndices = unique.Select(x => x.SiteId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index.ToString(), StringComparer.Ordinal);
    var mutantIndices = unique.Select(x => x.Id).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index.ToString(), StringComparer.Ordinal);
    return unique.Select(x => x with { SiteRuntimeId = siteIndices[x.SiteId], MutantRuntimeId = mutantIndices[x.Id] }).ToList();
  }

  private static string? GetSchemaCompatibilityFailure(MutationDescription description) => description.ApplicationKind switch
  {
    MutationApplicationKind.BuiltIn => null,
    MutationApplicationKind.ExpressionReplacement when description.Node is not ExpressionSyntax =>
        "Expression replacement schema requires an expression syntax node.",
    MutationApplicationKind.StatementReplacement when description.Node is not StatementSyntax =>
        "Statement replacement schema requires a statement syntax node.",
    MutationApplicationKind.ExpressionReplacement or MutationApplicationKind.StatementReplacement => null,
    _ => $"Unknown mutation application schema '{description.ApplicationKind}'."
  };

  internal static bool IsGeneratedSource(string path, string source)
  {
    var fileName = Path.GetFileName(path);
    if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
    {
      return true;
    }

    var headerLength = Math.Min(source.Length, 2048);
    return source.AsSpan(0, headerLength).Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
  }

  private static (bool Supported, string? Reason) IsSupportedMutationSite(ExpressionSyntax expression, SemanticModel model)
  {
    var operation = model.GetOperation(expression);
    var contextualArrayInitializer = expression is (ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax) &&
        model.GetTypeInfo(expression).Type is IArrayTypeSymbol ||
        expression is InitializerExpressionSyntax arrayInitializer && arrayInitializer.IsKind(SyntaxKind.ArrayInitializerExpression) &&
        arrayInitializer.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type: ArrayTypeSyntax arrayType } } } &&
        model.GetTypeInfo(arrayType).Type is IArrayTypeSymbol;
    if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression) &&
        (model.GetTypeInfo(binary.Left).Type?.SpecialType == SpecialType.System_String || model.GetTypeInfo(binary.Right).Type?.SpecialType == SpecialType.System_String))
    { return (false, "string concatenation is excluded"); }
    foreach (var syntax in expression.DescendantNodesAndSelf())
    {
      if (syntax is AwaitExpressionSyntax) { return (false, "await expressions cannot be wrapped without changing async semantics"); }
      if (syntax is not ExpressionSyntax current)
      {
        continue;
      }

      var type = model.GetTypeInfo(current).Type;
      var isArrayInitializer = syntax is InitializerExpressionSyntax initializer && initializer.IsKind(SyntaxKind.ArrayInitializerExpression);
      if (type?.TypeKind == TypeKind.Error && !(contextualArrayInitializer && isArrayInitializer))
      { return (false, "dynamic or unresolved operand type"); }
      var stackAllocation = expression is StackAllocArrayCreationExpressionSyntax;
      if (type?.IsRefLikeType == true && !stackAllocation ||
          type?.TypeKind == TypeKind.Pointer && !stackAllocation || type is ITypeParameterSymbol)
      { return (false, $"ref-like, pointer, or generic operand type '{type?.ToDisplayString()}' in '{current}'"); }
      if (current is IdentifierNameSyntax identifier && model.GetSymbolInfo(identifier).Symbol is ILocalSymbol local && (local.RefKind != RefKind.None || local.Type.IsRefLikeType))
      { return (false, "ref or ref-like local capture"); }
    }
    if (expression.Ancestors().Any(x => x is AttributeArgumentSyntax or CaseSwitchLabelSyntax or ConstantPatternSyntax) ||
        expression.Ancestors().OfType<ParameterSyntax>().Any(p => p.Default?.Value == expression) ||
        expression.Ancestors().OfType<VariableDeclaratorSyntax>().Any(v => v.Initializer?.Value == expression && v.Parent?.Parent is FieldDeclarationSyntax field && field.Modifiers.Any(SyntaxKind.ConstKeyword)) ||
        expression.Ancestors().OfType<LocalDeclarationStatementSyntax>().Any(d => d.Modifiers.Any(SyntaxKind.ConstKeyword) && d.Declaration.Variables.Any(v => v.Initializer?.Value == expression)))
    { return (false, "constant-expression context"); }
    if (operation == null && !contextualArrayInitializer)
    { return (false, "Roslyn could not bind the expression"); }
    return (true, null);
  }
  private static bool IsExpressionTreeContext(ExpressionSyntax expression, SemanticModel model) =>
      expression.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any(lambda =>
          model.GetTypeInfo(lambda).ConvertedType is INamedTypeSymbol named &&
          named.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>");
  private static bool HasExplicitOverflowContext(ExpressionSyntax expression) => expression.Ancestors().Any(node =>
      node.IsKind(SyntaxKind.CheckedExpression) || node.IsKind(SyntaxKind.UncheckedExpression) ||
      node.IsKind(SyntaxKind.CheckedStatement) || node.IsKind(SyntaxKind.UncheckedStatement));
  internal static void RewriteSources(Dictionary<string, string> files, List<Candidate> candidates, string projectRoot, string workspaceRoot,
      IReadOnlyDictionary<string, CSharpParseOptions>? parseOptionsByFile = null)
  {
    foreach (var (original, target) in files)
    {
      var fullTarget = Path.GetFullPath(target);
      var relativeTarget = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), fullTarget);
      if (Path.IsPathRooted(relativeTarget) || relativeTarget == ".." || relativeTarget.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
      {
        throw new InvalidOperationException($"Refusing to rewrite source '{original}' because its mapped path escapes the isolated workspace: '{fullTarget}'.");
      }

      if (!File.Exists(target))
      {
        continue;
      }

      var text = File.ReadAllText(target);
      var parseOptions = parseOptionsByFile?.GetValueOrDefault(original) ?? new CSharpParseOptions(LanguageVersion.Preview);
      var tree = CSharpSyntaxTree.ParseText(text, path: target, options: parseOptions);
      var local = candidates.Where(c => string.Equals(c.File, Path.GetRelativePath(projectRoot, original).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToArray();
      var root = tree.GetRoot();
      var statementProbeSites = local.Where(candidate => root.DescendantNodes().OfType<ReturnStatementSyntax>()
          .Any(statement => statement.Expression?.SpanStart == candidate.Start && statement.Expression.Span.Length == candidate.Length))
          .Select(candidate => candidate.SiteRuntimeId).ToHashSet(StringComparer.Ordinal);
      var rewritten = new SchemaRewriter(local, statementProbeSites).Visit(root)!;
      File.WriteAllText(target, rewritten.ToFullString());
    }
  }

  private sealed class SchemaRewriter(IReadOnlyList<Candidate> candidates, IReadOnlySet<string> statementProbeSites) : CSharpSyntaxRewriter
  {
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
      if (node is null)
      {
        return null;
      }

      var visited = base.Visit(node);
      if (node is ExpressionSyntax originalExpression && visited is ExpressionSyntax visitedExpression)
      {
        var extensionMutations = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.ExpressionReplacement &&
            candidate.Start == node.SpanStart && candidate.Length == node.Span.Length).ToArray();
        if (extensionMutations.Length == 0)
        {
          return visitedExpression;
        }

        var result = visitedExpression;
        foreach (var candidate in extensionMutations)
        {
          var activation = candidate.UseInlineSiteProbe
              ? $"MutationRuntime.IsActiveAndHit({candidate.SiteRuntimeId}, {candidate.MutantRuntimeId})"
              : $"MutationRuntime.IsActive({candidate.MutantRuntimeId})";
          result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression(activation),
              SyntaxFactory.ParenthesizedExpression(SyntaxFactory.ParseExpression(candidate.Replacement)), SyntaxFactory.ParenthesizedExpression(result));
        }

        var site = extensionMutations[0].SiteRuntimeId;
        if (statementProbeSites.Contains(site) || extensionMutations[0].UseInlineSiteProbe)
        {
          return result.WithTriviaFrom(originalExpression);
        }

        return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
            {
              SyntaxFactory.Argument(SyntaxFactory.ParseExpression(site)),
              SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(result))
            }))).WithTriviaFrom(originalExpression);
      }

      if (node is StatementSyntax originalStatement && visited is StatementSyntax visitedStatement)
      {
        var extensionMutations = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.StatementReplacement &&
            candidate.Start == node.SpanStart && candidate.Length == node.Span.Length).ToArray();
        if (extensionMutations.Length == 0)
        {
          return visitedStatement;
        }

        var statements = new List<StatementSyntax> { SyntaxFactory.ParseStatement($"MutationRuntime.Hit({extensionMutations[0].SiteRuntimeId});") };
        foreach (var candidate in extensionMutations)
        {
          statements.Add(SyntaxFactory.IfStatement(SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({candidate.MutantRuntimeId})"),
              SyntaxFactory.Block(SyntaxFactory.ParseStatement(candidate.Replacement)),
              SyntaxFactory.ElseClause(SyntaxFactory.Block(visitedStatement))));
        }
        return SyntaxFactory.Block(statements).WithTriviaFrom(originalStatement);
      }

      return visited;
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
      var visited = (BlockSyntax)base.VisitBlock(node)!;
      var blockMutations = candidates.Where(candidate => candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Operator == nameof(MutationCategory.Removal) && candidate.ReplacementToken == "removal:block").ToArray();
      if (blockMutations.Length == 0)
      {
        return visited;
      }

      var statements = new List<StatementSyntax>
      {
        SyntaxFactory.ParseStatement($"MutationRuntime.Hit({blockMutations[0].SiteRuntimeId});")
      };
      foreach (var candidate in blockMutations)
      {
        var mutation = SyntaxFactory.ParseStatement(candidate.Replacement);
        statements.Add(SyntaxFactory.IfStatement(SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({candidate.MutantRuntimeId})"),
            SyntaxFactory.Block(mutation), SyntaxFactory.ElseClause(visited)));
      }
      return SyntaxFactory.Block(statements).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
      var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
      StatementSyntax rewritten = visited;
      if (node.Expression is not null)
      {
        var entrySites = candidates.Where(candidate => candidate.Start == node.Expression.SpanStart &&
                candidate.Length == node.Expression.Span.Length && statementProbeSites.Contains(candidate.SiteRuntimeId))
            .Select(candidate => candidate.SiteRuntimeId).Distinct(StringComparer.Ordinal).ToArray();
        if (entrySites.Length > 0)
        {
          var hits = entrySites.Select(site => (StatementSyntax)SyntaxFactory.ParseStatement($"MutationRuntime.Hit({site});"));
          rewritten = SyntaxFactory.Block(hits.Append(visited)).WithTriviaFrom(node);
        }
      }

      var removal = RewriteRemovedStatement(node, rewritten);
      if (removal is not null)
      {
        return removal;
      }
      return rewritten;
    }

    public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitBreakStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitContinueStatement(ContinueStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitContinueStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitGotoStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitThrowStatement(ThrowStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitThrowStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitYieldStatement(YieldStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitYieldStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitExpressionStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    private BlockSyntax? RewriteRemovedStatement(StatementSyntax original, StatementSyntax visited)
    {
      var candidatesForStatement = candidates.Where(candidate => candidate.Start == original.SpanStart &&
          candidate.Length == original.Span.Length && candidate.ApplicationKind == MutationApplicationKind.BuiltIn &&
          candidate.Operator == nameof(MutationCategory.Removal)).ToArray();
      if (candidatesForStatement.Length == 0)
      {
        return null;
      }

      var body = new List<StatementSyntax> { SyntaxFactory.ParseStatement($"MutationRuntime.Hit({candidatesForStatement[0].SiteRuntimeId});") };
      foreach (var candidate in candidatesForStatement)
      {
        var mutated = SyntaxFactory.ParseStatement(candidate.Replacement);
        body.Add(SyntaxFactory.IfStatement(SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({candidate.MutantRuntimeId})"),
            SyntaxFactory.Block(mutated), SyntaxFactory.ElseClause(SyntaxFactory.Block(visited))));
      }
      return SyntaxFactory.Block(body).WithTriviaFrom(original);
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
      var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
      var ops = candidates.Where(c => c.ApplicationKind == MutationApplicationKind.BuiltIn && c.Start == node.SpanStart && c.Length == node.Span.Length && c.Operator != "Boolean").ToArray();
      if (ops.Any(candidate => candidate.Operator == MutationCategory.NullCoalescing.ToString()))
      {
        return RewriteExpression(node, visited, ops.Where(candidate => candidate.Operator == MutationCategory.NullCoalescing.ToString()).ToArray());
      }
      ExpressionSyntax result = visited;
      foreach (var c in ops)
      {
        var mut = c.ReplacementToken == "~^"
            ? SyntaxFactory.PrefixUnaryExpression(SyntaxKind.BitwiseNotExpression,
                SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(SyntaxKind.ExclusiveOrExpression, visited.Left, visited.Right)))
            : SyntaxFactory.ParseExpression($"({visited.Left}) {c.ReplacementToken} ({visited.Right})");
        var activation = c.UseInlineSiteProbe
            ? $"MutationRuntime.IsActiveAndHit({c.SiteRuntimeId}, {c.MutantRuntimeId})"
            : $"MutationRuntime.IsActive({c.MutantRuntimeId})";
        result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression(activation), SyntaxFactory.ParenthesizedExpression(mut), SyntaxFactory.ParenthesizedExpression(result));
      }
      // Record entry before evaluating the expression, including when it throws.
      if (ops.Length > 0 && !statementProbeSites.Contains(ops[0].SiteRuntimeId) && !ops[0].UseInlineSiteProbe)
      {
        result = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"), SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(SyntaxFactory.ParseExpression(ops[0].SiteRuntimeId)), SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(result)) })));
      }

      return result.WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
      var visited = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          (candidate.Operator is nameof(MutationCategory.Unary) or nameof(MutationCategory.Update) ||
           candidate.Operator == nameof(MutationCategory.Boolean) && candidate.ReplacementToken.StartsWith("unary:", StringComparison.Ordinal))).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitCheckedExpression(CheckedExpressionSyntax node)
    {
      var visited = (CheckedExpressionSyntax)base.VisitCheckedExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Checked)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitCollectionExpression(CollectionExpressionSyntax node)
    {
      var visited = (CollectionExpressionSyntax)base.VisitCollectionExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator is nameof(MutationCategory.CollectionExpression) or nameof(MutationCategory.Initialization)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
      var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator is nameof(MutationCategory.Initialization) or nameof(MutationCategory.Regex)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
      var visited = (ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node)!;
      return RewriteInitialization(node, visited);
    }

    public override SyntaxNode? VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
    {
      var visited = (ArrayCreationExpressionSyntax)base.VisitArrayCreationExpression(node)!;
      return RewriteInitialization(node, visited);
    }

    public override SyntaxNode? VisitImplicitArrayCreationExpression(ImplicitArrayCreationExpressionSyntax node)
    {
      var visited = (ImplicitArrayCreationExpressionSyntax)base.VisitImplicitArrayCreationExpression(node)!;
      return RewriteInitialization(node, visited);
    }

    public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
    {
      var visited = (VariableDeclaratorSyntax)base.VisitVariableDeclarator(node)!;
      var candidate = candidates.FirstOrDefault(mutation => mutation.ApplicationKind == MutationApplicationKind.BuiltIn && mutation.Start == node.SpanStart && mutation.Length == node.Span.Length &&
          mutation.Operator == nameof(MutationCategory.Initialization));
      if (candidate is null || node.Initializer?.Value is not InitializerExpressionSyntax initializer ||
          node.Parent is not VariableDeclarationSyntax { Type: ArrayTypeSyntax arrayType })
      {
        return visited;
      }

      var originalArray = $"new {arrayType} {initializer}";
      var value = SyntaxFactory.ParseExpression(
          $"MutationRuntime.Probe({candidate.SiteRuntimeId}, () => MutationRuntime.IsActive({candidate.MutantRuntimeId}) ? ({candidate.Replacement}) : ({originalArray}))");
      return visited.WithInitializer(visited.Initializer!.WithValue(value.WithTriviaFrom(visited.Initializer.Value)));
    }

    private ExpressionSyntax RewriteInitialization(ExpressionSyntax original, ExpressionSyntax visited)
    {
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == original.SpanStart && candidate.Length == original.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Initialization)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(original, visited, ops);
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
      var visited = (PostfixUnaryExpressionSyntax)base.VisitPostfixUnaryExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Update)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
      var visited = (ConditionalExpressionSyntax)base.VisitConditionalExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Conditional)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
    {
      var visited = (IsPatternExpressionSyntax)base.VisitIsPatternExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator is nameof(MutationCategory.Equality) or nameof(MutationCategory.Logical)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
      var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.String)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
      var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator is nameof(MutationCategory.String) or nameof(MutationCategory.StringMethods) or nameof(MutationCategory.Linq) or nameof(MutationCategory.Math)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
    {
      var visited = (InterpolatedStringExpressionSyntax)base.VisitInterpolatedStringExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.String)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
      var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
      var condition = RewriteCondition(node.Condition, visited.Condition);
      return condition == visited.Condition ? visited : visited.WithCondition(condition);
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
      var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
      var condition = RewriteCondition(node.Condition, visited.Condition);
      return condition == visited.Condition ? visited : visited.WithCondition(condition);
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
      var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
      var condition = RewriteCondition(node.Condition, visited.Condition);
      return condition == visited.Condition ? visited : visited.WithCondition(condition);
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
      var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
      if (node.Condition is null || visited.Condition is null)
      {
        return visited;
      }
      var condition = RewriteCondition(node.Condition, visited.Condition);
      return condition == visited.Condition ? visited : visited.WithCondition(condition);
    }

    private ExpressionSyntax RewriteCondition(ExpressionSyntax original, ExpressionSyntax visited)
    {
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == original.SpanStart && candidate.Length == original.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Boolean) && candidate.ReplacementToken == "boolean:negate-condition").ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(original, visited, ops);
    }

    private ExpressionSyntax RewriteExpression(SyntaxNode original, ExpressionSyntax visited, Candidate[] ops)
    {
      var result = visited;
      foreach (var candidate in ops)
      {
        var mutation = SyntaxFactory.ParseExpression(candidate.Replacement);
        var activation = candidate.UseInlineSiteProbe
            ? $"MutationRuntime.IsActiveAndHit({candidate.SiteRuntimeId}, {candidate.MutantRuntimeId})"
            : $"MutationRuntime.IsActive({candidate.MutantRuntimeId})";
        result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression(activation),
            SyntaxFactory.ParenthesizedExpression(mutation), SyntaxFactory.ParenthesizedExpression(result));
      }
      if (ops.Length != 0 && !statementProbeSites.Contains(ops[0].SiteRuntimeId) && !ops[0].UseInlineSiteProbe)
      {
        var probeMethod = string.IsNullOrWhiteSpace(ops[0].ResultType) ? "MutationRuntime.Probe" : $"MutationRuntime.Probe<{ops[0].ResultType}>";
        result = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(probeMethod),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
                SyntaxFactory.Argument(SyntaxFactory.ParseExpression(ops[0].SiteRuntimeId)),
                SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(result)) })));
      }
      return result.WithTriviaFrom(original);
    }
    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
      var visited = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length && candidate.Operator == MutationCategory.Assignment.ToString()).ToArray();
      ExpressionSyntax result = visited;
      foreach (var candidate in ops)
      {
        var mutation = SyntaxFactory.ParseExpression($"{visited.Left} {candidate.ReplacementToken} ({visited.Right})");
        var activation = candidate.UseInlineSiteProbe
            ? $"MutationRuntime.IsActiveAndHit({candidate.SiteRuntimeId}, {candidate.MutantRuntimeId})"
            : $"MutationRuntime.IsActive({candidate.MutantRuntimeId})";
        result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression(activation),
            SyntaxFactory.ParenthesizedExpression(mutation), SyntaxFactory.ParenthesizedExpression(result));
      }
      if (ops.Length > 0 && !statementProbeSites.Contains(ops[0].SiteRuntimeId) && !ops[0].UseInlineSiteProbe)
      {
        result = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
                        SyntaxFactory.Argument(SyntaxFactory.ParseExpression(ops[0].SiteRuntimeId)),
                        SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(result)) })));
      }

      return result.WithTriviaFrom(node);
    }
    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
      var c = candidates.FirstOrDefault(x => x.ApplicationKind == MutationApplicationKind.BuiltIn && x.Operator == "Boolean" && x.Start == node.SpanStart); if (c == null)
      {
        var stringOps = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
            candidate.Operator == nameof(MutationCategory.String)).ToArray();
        var visited = (LiteralExpressionSyntax)base.VisitLiteralExpression(node)!;
        return stringOps.Length == 0 ? visited : RewriteExpression(node, visited, stringOps);
      }

      var activation = c.UseInlineSiteProbe
          ? $"MutationRuntime.IsActiveAndHit({c.SiteRuntimeId}, {c.MutantRuntimeId})"
          : $"MutationRuntime.IsActive({c.MutantRuntimeId})";
      var expr = (ExpressionSyntax)SyntaxFactory.ParseExpression($"{activation} ? {c.Replacement} : {node}").WithTriviaFrom(node);
      if (statementProbeSites.Contains(c.SiteRuntimeId) || c.UseInlineSiteProbe)
      {
        return expr.WithTriviaFrom(node);
      }

      return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"), SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(SyntaxFactory.ParseExpression(c.SiteRuntimeId)), SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(expr)) }))).WithTriviaFrom(node);
    }
  }

  private static void WriteRuntimeSources(string workspaceCopy, string solutionRoot, IReadOnlyList<string> productionProjects, IReadOnlyList<Candidate> candidates)
  {
    var source = RuntimeSource(candidates.Select(candidate => candidate.SiteRuntimeId).Distinct(StringComparer.Ordinal).Count());
    foreach (var project in productionProjects)
    {
      var projectDirectory = Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, Path.GetDirectoryName(project)!));
      File.WriteAllText(Path.Combine(projectDirectory, "MutationRuntime.g.cs"), source);
    }
  }

  internal static string RuntimeSource(int siteCount) => $$"""
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Runtime.CompilerServices;
    internal static class MutationRuntime
    {
      static readonly string ActiveMutationIds = Environment.GetEnvironmentVariable("MUTATION_ACTIVE_ID") ?? string.Empty;
      static readonly int Active = int.TryParse(ActiveMutationIds, out var id) ? id : -1;
      static readonly System.Collections.Generic.HashSet<int>? ActiveSet = Active >= 0 ? null : ActiveMutationIds
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => int.TryParse(value, out var id) ? id : -1).Where(id => id >= 0).ToHashSet();
      static readonly string ProbePath = Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE") ?? string.Empty;
      static readonly bool PerTestCoverageEnabled = Environment.GetEnvironmentVariable("MUTATION_PER_TEST_COVERAGE") == "1";
      static readonly bool ProbeEnabled = !string.IsNullOrEmpty(ProbePath) || PerTestCoverageEnabled;
      static readonly int[] Hits = ProbeEnabled ? new int[{{siteCount}}] : Array.Empty<int>();
      [ModuleInitializer] internal static void Initialize() { if (ProbeEnabled) { _ = Hits.Length; } }
      static MutationRuntime()
      {
        if (!ProbeEnabled || PerTestCoverageEnabled) { return; }
        AppDomain.CurrentDomain.ProcessExit += (_,__) =>
        {
          var timer = Stopwatch.StartNew();
          var ids = JsonSerializer.Serialize(Enumerable.Range(0, Hits.Length).Where(index => Volatile.Read(ref Hits[index]) != 0));
          timer.Stop();
          File.AppendAllText(ProbePath, "{\"siteIds\":" + ids + ",\"serializationMs\":" + timer.ElapsedMilliseconds + "}" + Environment.NewLine);
        };
      }
      public static bool IsActive(int id) => Active == id || ActiveSet?.Contains(id) == true;
      public static void Hit(int id) { if (ProbeEnabled && Volatile.Read(ref Hits[id]) == 0) { Interlocked.CompareExchange(ref Hits[id], 1, 0); } }
      public static bool IsActiveAndHit(int siteId, int mutantId) { if (ProbeEnabled) { Hit(siteId); } return IsActive(mutantId); }
      public static int[] GetCoverageData()
      {
        var sites = new System.Collections.Generic.List<int>();
        for (var index = 0; index < Hits.Length; index++)
        {
          if (Volatile.Read(ref Hits[index]) != 0 && Interlocked.Exchange(ref Hits[index], 0) != 0) { sites.Add(index); }
        }
        return sites.ToArray();
      }
      public static T Probe<T>(int id, Func<T> value) { if (ProbeEnabled) { Hit(id); } return value(); }
    }
    """;

  internal sealed record ProbeRead(string[] Sites, long SerializationMs, CoverageStatus Status = CoverageStatus.CoverageSuccess);
  private static async Task<ProbeRead> ReadProbeSitesCached(string path, List<Candidate> candidates, string testId,
      string[] stableAssemblyIdentities, ArtifactCache cache,
      List<string> diagnostics, CancellationToken cancellationToken)
  {
    // Baseline tests and probe parsing always run. Only the deterministic runtime-site-to-site projection is cached.
    var observed = ReadProbeRuntimeSiteIds(path);
    return await ReadCoverageSitesCached(observed.RuntimeSiteIds, testId, stableAssemblyIdentities, candidates,
        cache, diagnostics, cancellationToken, observed.SerializationMs);
  }

  private static async Task<ProbeRead> ReadCoverageSitesCached(string[] runtimeSiteIds, string testId,
      string[] stableAssemblyIdentities, List<Candidate> candidates, ArtifactCache cache,
      List<string> diagnostics, CancellationToken cancellationToken, long serializationMs = 0)
  {
    var observedRuntimeSiteIds = runtimeSiteIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    var mapping = candidates.Select(candidate => candidate.SiteRuntimeId + "=" + candidate.SiteId)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    var keyValues = new[]
        { "coverage-map-v1", testId, string.Join("|", observedRuntimeSiteIds), string.Join("|", mapping),
          string.Join("|", stableAssemblyIdentities.Order(StringComparer.OrdinalIgnoreCase)),
          EnvironmentFingerprint(), EngineVersion(), typeof(TestPlatformSession).Assembly.ManifestModule.ModuleVersionId.ToString("N") };
    var key = ArtifactCache.ContentKey([], keyValues.Order(StringComparer.Ordinal));
    var cacheRead = await cache.TryReadAsync<CachedCoverageMap>("coverage-maps", testId, key, cancellationToken);
    if (cacheRead.Found &&
        cacheRead.Value is { } cached &&
        cached?.RuntimeSiteIds is not null && cached.Sites is not null &&
        cached.RuntimeSiteIds.SequenceEqual(observedRuntimeSiteIds, StringComparer.Ordinal) &&
        cached.Sites.SequenceEqual(cached.Sites.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        cached.Sites.All(site => candidates.Any(candidate => string.Equals(candidate.SiteId, site, StringComparison.Ordinal))) &&
        string.Equals(cached.IntegrityHash, CoverageMapIntegrity(cached.RuntimeSiteIds, cached.Sites), StringComparison.Ordinal))
    {
      diagnostics.Add(cacheRead.Diagnostic);
      return new(cached.Sites, serializationMs);
    }

    diagnostics.Add(cacheRead.Diagnostic.StartsWith("CacheHit:", StringComparison.Ordinal)
        ? $"CacheInvalidated: coverage-maps {key} (cached mapping failed validation)"
        : cacheRead.Diagnostic);
    var sites = candidates.Where(candidate => observedRuntimeSiteIds.Contains(candidate.SiteRuntimeId, StringComparer.Ordinal))
        .Select(candidate => candidate.SiteId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    var result = new CachedCoverageMap(observedRuntimeSiteIds, sites, CoverageMapIntegrity(observedRuntimeSiteIds, sites));
    await cache.WriteAsync("coverage-maps", testId, key, result, cancellationToken);
    return new(sites, serializationMs);
  }

  private static string CoverageMapIntegrity(IEnumerable<string> runtimeSiteIds, IEnumerable<string> sites) =>
      ArtifactCache.ContentKey([], ["coverage-map-record-v1", .. runtimeSiteIds, "sites", .. sites]);

  internal static ProbeRead ReadProbeSites(string path, List<Candidate> c)
  {
    var probe = ReadProbeRuntimeSiteIds(path);
    var ids = probe.RuntimeSiteIds.ToHashSet(StringComparer.Ordinal);
    return new(c.Where(x => ids.Contains(x.SiteRuntimeId)).Select(x => x.SiteId).Distinct(StringComparer.Ordinal).ToArray(), probe.SerializationMs);
  }

  private sealed record ParsedProbe(string[] RuntimeSiteIds, long SerializationMs);
  private static ParsedProbe ReadProbeRuntimeSiteIds(string path)
  {
    if (!File.Exists(path))
    {
      throw new CoverageCollectionException(CoverageStatus.CoverageUnavailable, $"CoverageUnavailable: probe artifact was not produced: {path}");
    }

    try
    {
      var documents = File.ReadAllText(path).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
          .Select(line => JsonDocument.Parse(line)).ToArray();
      if (documents.Length == 0)
      {
        throw new JsonException("Probe artifact is empty.");
      }

      var values = new HashSet<int>(); long serializationMs = 0;
      foreach (var document in documents)
      {
        using (document)
        {
          var root = document.RootElement;
          var sites = root.ValueKind == JsonValueKind.Array
              ? root.Deserialize<int[]>() ?? throw new JsonException("Probe artifact contains null.")
              : root.GetProperty("siteIds").Deserialize<int[]>() ?? throw new JsonException("Probe artifact siteIds contains null.");
          values.UnionWith(sites);
          if (root.ValueKind != JsonValueKind.Array)
          {
            var duration = root.GetProperty("serializationMs").GetInt64();
            if (duration < 0)
            {
              throw new JsonException("Probe artifact has a negative serialization duration.");
            }

            serializationMs += duration;
          }
        }
      }
      return new(values.Select(x => x.ToString()).Order(StringComparer.Ordinal).ToArray(), serializationMs);
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
    {
      throw new CoverageCollectionException(CoverageStatus.CoverageCorrupted, $"CoverageCorrupted: probe artifact is unreadable or corrupted: {path}", ex);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      throw new CoverageCollectionException(CoverageStatus.CoverageCollectionFailed, $"CoverageCollectionFailed: unable to read probe artifact: {path}", ex);
    }
  }
  private static string[] ParseTests(string text)
  {
    var result = new List<string>(); var reading = false;
    foreach (var raw in text.Split('\n'))
    {
      var line = raw.Trim(); if (line.StartsWith("The following Tests are available:", StringComparison.OrdinalIgnoreCase)) { reading = true; continue; }
      if (reading && line.Length > 0 && !line.StartsWith("Test run", StringComparison.OrdinalIgnoreCase))
      {
        result.Add(line.TrimEnd('\r'));
      }
    }
    return result.ToArray();
  }
  internal static List<(Candidate Candidate, string Diagnostic)> FindInvalidCandidates(string buildOutput, IReadOnlyList<Candidate> candidates, string appCopy)
  {
    const string diagnosticPattern = @"^(?<path>.+?)\((?<line>\d+),(?<column>\d+)\): error (?<code>CS\d+): (?<message>.*)$";
    var failures = new Dictionary<string, (Candidate Candidate, string Diagnostic)>(StringComparer.Ordinal);
    foreach (Match match in Regex.Matches(buildOutput, diagnosticPattern, RegexOptions.Multiline))
    {
      var file = Path.GetFullPath(match.Groups["path"].Value.Trim());
      var line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
      var diagnostic = $"{match.Groups["code"].Value}: {match.Groups["message"].Value.Trim()}";
      foreach (var candidate in candidates.Where(candidate => candidate.Line == line &&
                   string.Equals(Path.GetFullPath(Path.Combine(appCopy, candidate.File)), file, StringComparison.OrdinalIgnoreCase)))
      {
        failures.TryAdd(candidate.Id, (candidate, diagnostic));
      }
    }
    return failures.Values.OrderBy(failure => failure.Candidate.File, StringComparer.Ordinal)
        .ThenBy(failure => failure.Candidate.Line).ThenBy(failure => failure.Candidate.Column)
        .ThenBy(failure => failure.Candidate.Id, StringComparer.Ordinal).ToList();
  }

  private static void ReindexCandidates(List<Candidate> candidates)
  {
    var siteIndices = candidates.Select(candidate => candidate.SiteId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index.ToString(), StringComparer.Ordinal);
    var mutationIndices = candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index.ToString(), StringComparer.Ordinal);
    for (var index = 0; index < candidates.Count; index++)
    {
      candidates[index] = candidates[index] with { SiteRuntimeId = siteIndices[candidates[index].SiteId], MutantRuntimeId = mutationIndices[candidates[index].Id] };
    }
  }

  internal static Dictionary<string, string> MaterializeLinkedSources(string workspaceCopy, string solutionRoot,
      IReadOnlyList<LinkedCompileItem> linkedCompileItems)
  {
    var sourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in linkedCompileItems)
    {
      if (!IsWithinDirectory(item.ProjectPath, solutionRoot))
      {
        throw new InvalidOperationException($"Linked source '{item.File}' is owned by a project outside the copied solution tree; linked source remapping is unsupported for that layout.");
      }

      var linkedDirectory = Path.Combine(workspaceCopy, ".mutagen-linked");
      Directory.CreateDirectory(linkedDirectory);
      var destination = Path.Combine(linkedDirectory, Hash(Path.GetFullPath(item.File).ToUpperInvariant())[..16] + "_" + Path.GetFileName(item.File));
      if (!File.Exists(destination))
      {
        File.Copy(item.File, destination);
      }
      sourceMap[item.File] = destination;
    }

    foreach (var group in linkedCompileItems.GroupBy(item => item.ProjectPath, StringComparer.OrdinalIgnoreCase))
    {
      var ownerProjectCopy = Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, group.Key));
      var document = XDocument.Load(ownerProjectCopy, LoadOptions.PreserveWhitespace);
      var itemGroup = new XElement(document.Root!.Name.Namespace + "ItemGroup");
      foreach (var linkedFile in group.GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase))
      {
        var item = linkedFile.First();
        var identities = linkedFile.Select(candidate => candidate.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (identities.Any(string.IsNullOrWhiteSpace))
        {
          throw new InvalidOperationException($"Linked source '{item.File}' has no evaluated Compile identity in '{group.Key}'.");
        }

        var mappedInclude = Path.GetRelativePath(Path.GetDirectoryName(ownerProjectCopy)!, sourceMap[item.File]);
        foreach (var identity in identities)
        {
          itemGroup.Add(new XElement(document.Root.Name.Namespace + "Compile", new XAttribute("Remove", identity)));
        }
        itemGroup.Add(new XElement(document.Root.Name.Namespace + "Compile", new XAttribute("Include", mappedInclude)));
      }
      document.Root.Add(itemGroup);
      document.Save(ownerProjectCopy, SaveOptions.DisableFormatting);
    }

    return sourceMap;
  }

  internal static void StageExternalWorkspaceConfiguration(string workspaceCopy, string solutionRoot,
      IReadOnlyList<ProjectEvaluation> evaluations, IReadOnlyList<LinkedCompileItem> linkedCompileItems,
      IReadOnlyDictionary<string, string> linkedSourceMap)
  {
    var fullRoot = Path.GetFullPath(solutionRoot);
    var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (string.IsNullOrWhiteSpace(packageRoot))
    {
      packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    packageRoot = Path.GetFullPath(packageRoot);
    var projects = evaluations.SelectMany(evaluation => evaluation.ProjectPaths).Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var supportedExternalImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" })
    {
      var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var project in projects)
      {
        var configuration = FindNearestConfiguration(project, name);
        if (configuration is null || IsWithinDirectory(configuration, fullRoot))
        {
          continue;
        }

        imports[project] = configuration;
        CollectStaticImports(configuration, supportedExternalImports);
      }

      if (imports.Count == 0)
      {
        continue;
      }

      if (name == "global.json")
      {
        var distinctConfigs = imports.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinctConfigs.Length != 1)
        {
          throw new InvalidOperationException("Projects in the copied solution resolve different external global.json files; select one SDK configuration for the mutation workspace.");
        }

        var destination = Path.Combine(workspaceCopy, name);
        if (!File.Exists(destination))
        {
          File.Copy(distinctConfigs[0], destination);
        }
      }
      else
      {
        var wrapperPath = Path.Combine(workspaceCopy, name);
        if (File.Exists(wrapperPath))
        {
          throw new InvalidOperationException($"The copied solution contains '{name}' but one or more projects resolve an external copy. Nested configuration resolution cannot be represented safely.");
        }

        var wrapper = new XDocument(new XElement("Project"));
        foreach (var (project, importPath) in imports.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
          var clonedProject = Path.Combine(workspaceCopy, Path.GetRelativePath(fullRoot, project));
          wrapper.Root!.Add(new XElement("Import",
              new XAttribute("Project", importPath),
              new XAttribute("Condition", $"'$(MSBuildProjectFullPath)' == '{clonedProject}'")));
        }

        if (name == "Directory.Build.targets")
        {
          AddExternalLinkedCompileRemapping(wrapper.Root!, workspaceCopy, fullRoot, linkedCompileItems);
        }

        wrapper.Save(wrapperPath, SaveOptions.DisableFormatting);
      }
    }

    foreach (var project in projects)
    {
      var inputs = ProjectConfigurationInputs(project, out _);
      var nearestConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" })
      {
        var nearest = FindNearestConfiguration(project, name);
        if (nearest is not null)
        {
          nearestConfigs.Add(nearest);
        }
      }

      var externalConfiguration = inputs.FirstOrDefault(path =>
      {
        if (IsWithinDirectory(path, fullRoot) || IsWithinDirectory(path, packageRoot))
        {
          return false;
        }

        if (supportedExternalImports.Contains(path))
        {
          return false;
        }

        var name = Path.GetFileName(path);
        if (name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("global.json", StringComparison.OrdinalIgnoreCase))
        {
          // Ancestor files shadowed by a nearer configuration are not imported by MSBuild.
          return nearestConfigs.Contains(path);
        }

        return Path.GetExtension(path).Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase);
      });
      if (externalConfiguration is not null)
      {
        throw new InvalidOperationException($"Project '{project}' imports unsupported configuration outside the copied solution tree ('{externalConfiguration}'). External Directory.Build.props, Directory.Build.targets, Directory.Packages.props, and global.json are staged into the isolated workspace; other external configuration must be moved under the solution tree.");
      }
    }
  }

  private static string? FindNearestConfiguration(string project, string fileName)
  {
    for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(project))!); directory is not null; directory = directory.Parent)
    {
      var path = Path.Combine(directory.FullName, fileName);
      if (File.Exists(path))
      {
        return Path.GetFullPath(path);
      }
    }

    return null;
  }

  private static void CollectStaticImports(string path, HashSet<string> importedFiles)
  {
    if (!File.Exists(path) || !importedFiles.Add(path))
    {
      return;
    }

    XDocument document;
    try { document = XDocument.Load(path); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
    {
      return;
    }

    foreach (var import in document.Descendants().Where(element => element.Name.LocalName == "Import"))
    {
      var project = import.Attribute("Project")?.Value;
      if (string.IsNullOrWhiteSpace(project))
      {
        continue;
      }

      project = project.Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
          .Replace("$(MSBuildThisFileFullPath)", path, StringComparison.OrdinalIgnoreCase)
          .Replace("$(NuGetPackageRoot)", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
      if (project.Contains("$(", StringComparison.Ordinal) || project.IndexOfAny(['*', '?']) >= 0)
      {
        continue;
      }

      var resolved = Path.GetFullPath(Path.IsPathRooted(project) ? project : Path.Combine(Path.GetDirectoryName(path)!, project));
      if (File.Exists(resolved))
      {
        CollectStaticImports(resolved, importedFiles);
      }
    }
  }

  private static void AddExternalLinkedCompileRemapping(XElement wrapper, string workspaceCopy, string solutionRoot,
      IReadOnlyList<LinkedCompileItem> linkedCompileItems)
  {
    foreach (var projectGroup in linkedCompileItems.Where(item => !IsWithinDirectory(item.DefiningProjectPath, solutionRoot))
                 .GroupBy(item => item.ProjectPath, StringComparer.OrdinalIgnoreCase))
    {
      var clonedProject = Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, projectGroup.Key));
      var itemGroup = new XElement("ItemGroup",
          new XAttribute("Condition", $"'$(MSBuildProjectFullPath)' == '{clonedProject}'"));
      foreach (var sourceGroup in projectGroup.GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase))
      {
        var identities = sourceGroup.Select(item => item.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (identities.Any(string.IsNullOrWhiteSpace))
        {
          throw new InvalidOperationException($"Linked source '{sourceGroup.Key}' has no evaluated Compile identity in '{projectGroup.Key}'.");
        }

        foreach (var identity in identities)
        {
          itemGroup.Add(new XElement("Compile", new XAttribute("Remove", identity)));
        }
      }
      wrapper.Add(itemGroup);
    }
  }

  private static bool IsWithinDirectory(string path, string directory)
  {
    var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
    return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
  }

  internal static string FindProjectTreeRoot(IReadOnlyList<string> projectPaths)
  {
    if (projectPaths.Count == 0)
    {
      throw new ArgumentException("At least one evaluated project path is required.", nameof(projectPaths));
    }

    var projectDirectories = projectPaths.Select(project => Path.GetDirectoryName(Path.GetFullPath(project))!)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var nugetConfigurationDirectories = projectDirectories.SelectMany(directory => AncestorDirectories(directory)
            .Where(ancestor => File.Exists(Path.Combine(ancestor, "NuGet.config"))))
        .Distinct(StringComparer.OrdinalIgnoreCase);
    var directories = projectDirectories.Concat(nugetConfigurationDirectories).ToArray();
    var common = directories[0];
    while (directories.Any(directory => !IsWithin(directory, common)))
    {
      var parent = Directory.GetParent(common)?.FullName;
      if (parent is null || string.Equals(parent, common, StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException("Production and test projects do not share a safe project-tree root. Place them under a common solution directory.");
      }

      common = parent;
    }
    return common;

    static IEnumerable<string> AncestorDirectories(string directory)
    {
      for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
      {
        yield return current.FullName;
      }
    }

    static bool IsWithin(string path, string root)
    {
      var relative = Path.GetRelativePath(root, path);
      return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
  }

  private static (int Files, long Bytes) CopyProject(string from, string to)
  {
    static bool Excluded(string path) => path.Split(Path.DirectorySeparatorChar).Any(part =>
        part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        part.Equals(".mutagen-cache", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("artifacts", StringComparison.OrdinalIgnoreCase) || part.Equals(".codex", StringComparison.OrdinalIgnoreCase) ||
        part.Equals(".aws", StringComparison.OrdinalIgnoreCase));
    Directory.CreateDirectory(to);
    var pending = new Stack<string>();
    pending.Push(from);
    var copiedFiles = 0;
    long copiedBytes = 0;
    while (pending.TryPop(out var sourceDirectory))
    {
      var destinationDirectory = Path.Combine(to, Path.GetRelativePath(from, sourceDirectory));
      Directory.CreateDirectory(destinationDirectory);
      foreach (var file in Directory.GetFiles(sourceDirectory))
      {
        File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), true);
        copiedFiles++;
        copiedBytes += new FileInfo(file).Length;
      }

      foreach (var directory in Directory.GetDirectories(sourceDirectory))
      {
        if (!Excluded(Path.GetRelativePath(from, directory)))
        {
          pending.Push(directory);
        }
      }
    }

    return (copiedFiles, copiedBytes);
  }
  private static string Html(MutationRunResult r) { static string E(string s) => System.Net.WebUtility.HtmlEncode(s); var t = r.Timings; var c = r.Counters; var b = new StringBuilder("<!doctype html><meta charset=utf-8><title>Mutation report</title><style>body{font:15px system-ui;max-width:1100px;margin:2rem auto}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:.5rem;text-align:left}code{white-space:pre-wrap}.Killed{color:#a00}.Survived{color:#087}.Unsupported{color:#765}</style>"); b.Append($"<h1>Mutation report</h1><p>Status: {E(r.OverallStatus)} | Score: {(r.MutationScore?.ToString("F1") + "%" ?? "n/a")} | Total: {r.Mutants.Count} | Killed: {r.KilledCount} | Survived: {r.SurvivedCount} | No coverage: {r.NoCoverageCount} | Unsupported: {r.UnsupportedMutations.Count} | Total duration: {r.TotalDurationMs} ms</p><h2>Exclusive phase timings (ms)</h2><p>Project evaluation {t.ProjectEvaluationMs}; source discovery {t.DiscoveryMs}; source copy {t.ProjectCopyMs}; Roslyn/instrumentation {t.InstrumentationMs}; build {t.BuildMs}; baseline tests {t.BaselineMs}; probe serialization {t.ProbeSerializationMs}; coverage mapping {t.CoverageMappingMs}; mutation generation {t.MutationGenerationMs}; planning {t.MutationPlanningMs}; worker initialization {t.WorkerInitializationMs}; mutation execution {Math.Max(0, t.ExecutionMs - t.WorkerInitializationMs)}; reporting {t.ReportingMs}.</p><p>Execution mode: {E(r.ExecutionPlan?.SelectedMode.ToString() ?? "not executed")}; planned batches: {r.ExecutionPlan?.Batches.Count ?? 0}; conflict edges: {r.ExecutionPlan?.Statistics.ConflictEdges ?? 0}.</p><h2>Execution counters</h2><p>Mutants: {c.TotalMutants} total, {c.CoveredMutants} covered, {c.UncoveredMutants} uncovered; tests {c.TotalTests}; baseline executions {c.BaselineTestExecutions}; mutation executions {c.MutationTestExecutions}; controller/test runner starts {c.TestRunnerProcessStarts}; testhost starts {c.TestHostProcessStarts}; discovery operations {c.TestDiscoveryOperations}; builds {c.BuildInvocations}; worker restarts {c.WorkerRestarts}; worker failures {c.WorkerFailures}; execution units {c.MutationBatches}, average mutants/unit {c.AverageMutantsPerBatch:F2}; worker busy {c.TotalWorkerBusyTimeMs} ms, idle {c.TotalWorkerIdleTimeMs} ms; observed process starts {r.ProcessStarts}.</p><table><thead><tr><th>Status</th><th>Source</th><th>Operator</th><th>Transformation</th><th>Tests</th></tr></thead><tbody>"); foreach (var m in r.Mutants) { b.Append($"<tr><td class='{m.Status}'>{m.Status}</td><td>{E(m.SourceFile)}:{m.Line}:{m.Column}</td><td>{E(m.Operator)}</td><td><code>{E(m.Original)} &#8594; {E(m.Replacement)}</code></td><td>{E(string.Join(", ", m.CoveringTests))}</td></tr>"); } foreach (var m in r.UnsupportedMutations) { b.Append($"<tr><td class='Unsupported'>Unsupported</td><td>{E(m.SourceFile)}:{m.Line}:{m.Column}</td><td>{E(m.Operator)}</td><td><code>{E(m.Original)} &#8594; {E(m.Replacement)}</code></td><td>{E(m.Diagnostic ?? "")}</td></tr>"); } return b.Append("</tbody></table>").ToString(); }
  private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
internal sealed class BaselineFailureException(string message, IReadOnlyList<string> errors) : Exception(message)
{
  public IReadOnlyList<string> Errors { get; } = errors;
  public override string ToString() => base.ToString() + Environment.NewLine + string.Join(Environment.NewLine, Errors);
}
internal sealed class CoverageCollectionException(CoverageStatus status, string message, Exception? inner = null) : Exception(message, inner)
{
  public CoverageStatus Status { get; } = status;
}
