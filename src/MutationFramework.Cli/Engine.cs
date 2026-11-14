using System.Diagnostics;
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
public sealed record TestIdentity(string Id, string Name, string DisplayName, long DurationMs = 0, string ExecutorUri = "", string SourceAssembly = "", string? ParameterizedCaseIdentity = null);
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
    public ExecutionCounters Counters { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public int ProcessStarts { get; init; }
    public IReadOnlyList<TestIdentity> Tests { get; init; } = [];
    public MutationExecutionPlan? ExecutionPlan { get; init; }
    public IReadOnlyDictionary<string, CoverageStatus> CoverageStatuses { get; init; } = new Dictionary<string, CoverageStatus>();
}
public sealed record MutationCandidateInfo(string Id, string SiteId, string Operator, string SourceFile, int Line, int Column, string Original, string Replacement);
public sealed record MutationDiscoveryResult(IReadOnlyList<MutationCandidateInfo> Candidates, IReadOnlyList<TestIdentity> Tests, IReadOnlyList<string> Diagnostics, long DurationMs, int ProcessStarts);
public sealed class MutationOptions
{
    public required string ProjectPath { get; init; }
    public required IReadOnlyList<string> TestProjects { get; init; }
    public int MaxWorkers { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    public string OutputDirectory { get; init; } = "mutation-results";
    public string Report { get; init; } = "json,html";
    public int TimeoutSeconds { get; init; } = 120;
    public MutationExecutionMode ExecutionMode { get; init; } = MutationExecutionMode.Auto;
    public string? TestFilter { get; init; }
    public string? Include { get; init; }
    public string? Exclude { get; init; }
}

public static class MutationEngine
{
    public static async Task<MutationRunResult> RunAsync(MutationOptions options, CancellationToken cancellationToken = default) => await Engine.RunAsync(options, true, cancellationToken);
    public static async Task<MutationRunResult> PlanAsync(MutationOptions options, CancellationToken cancellationToken = default) => await Engine.RunAsync(options, false, cancellationToken);
    public static async Task<MutationDiscoveryResult> DiscoverAsync(MutationOptions options, CancellationToken cancellationToken = default) => await Engine.DiscoverAsync(options, cancellationToken);
}

internal sealed record Candidate(string Id, string SiteId, string Operator, string File, int Start, int Length, int Line, int Column, string Original, string Replacement, string ReplacementToken, string SiteRuntimeId, string MutantRuntimeId);
internal sealed record TestRun(string Name, bool Passed, bool Skipped, long DurationMs, string? Error) { public string ProbePath { get; init; } = ""; }
internal sealed class RunMetrics
{
    public int ProcessStarts, TestRunnerStarts, TestHostStarts, BuildInvocations, DiscoveryOperations, BaselineTests, MutationTests, WorkerRestarts, WorkerFailures;
    public void StartedProcess(string exe, IReadOnlyList<string> args)
    {
        Interlocked.Increment(ref ProcessStarts);
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Contains("test", StringComparer.OrdinalIgnoreCase)) Interlocked.Increment(ref TestRunnerStarts);
            if (args.Contains("build", StringComparer.OrdinalIgnoreCase)) Interlocked.Increment(ref BuildInvocations);
            if (args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase)) Interlocked.Increment(ref DiscoveryOperations);
        }
    }
    public void StartedTestRunner() { Interlocked.Increment(ref ProcessStarts); Interlocked.Increment(ref TestRunnerStarts); }
    public void StartedTestHost(int count = 1) { Interlocked.Add(ref ProcessStarts, count); Interlocked.Add(ref TestHostStarts, count); }
}

internal static class Engine
{
    public static async Task<MutationDiscoveryResult> DiscoverAsync(MutationOptions opt, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew(); var metrics = new RunMetrics();
        string app = Path.GetFullPath(opt.ProjectPath);
        var testProjects = opt.TestProjects.Select(Path.GetFullPath).ToArray();
        if (testProjects.Length == 0) throw new ArgumentException("At least one test project is required.");
        if (!File.Exists(app) || testProjects.Any(t => !File.Exists(t))) throw new FileNotFoundException("Project or test project does not exist.");
        var diagnostics = new List<string>();
        var candidates = DiscoverCandidates(await GetCompileFiles(app, metrics, ct), app, opt, diagnostics, ct);
        var testDlls = new List<string>();
        foreach (var test in testProjects)
        {
            var build = await RunProcess("dotnet", ["build", test, "-c", "Debug", "--nologo", "-v:q"], Path.GetDirectoryName(test)!, null, opt.TimeoutSeconds, metrics, ct);
            if (build.ExitCode != 0) throw new InvalidOperationException($"Test project build failed ({test}). " + build.Output);
            testDlls.Add(FindTestAssembly(test));
        }
        using var platform = new TestPlatformSession(TestPlatformPaths.FindConsole(testProjects[0]), metrics.StartedTestRunner);
        metrics.DiscoveryOperations++;
        var discoveredTests = platform.Discover(testDlls, ct);
        metrics.StartedTestHost(testDlls.Count);
        var testIdentities = discoveredTests
            .Where(t => opt.TestFilter == null || t.FullyQualifiedName.Contains(opt.TestFilter, StringComparison.OrdinalIgnoreCase) || t.DisplayName.Contains(opt.TestFilter, StringComparison.OrdinalIgnoreCase))
            .Select(t => new TestIdentity(t.Id, t.FullyQualifiedName, t.DisplayName, 0, t.ExecutorUri, t.SourceAssembly, t.Case.Id.ToString("N")))
            .OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
        return new(candidates.Select(c => new MutationCandidateInfo(c.Id, c.SiteId, c.Operator, c.File, c.Line, c.Column, c.Original, c.Replacement)).ToArray(), testIdentities, diagnostics, sw.ElapsedMilliseconds, metrics.ProcessStarts);
    }

    public static async Task<MutationRunResult> RunAsync(MutationOptions opt, bool executeMutants, CancellationToken ct)
    {
        if (opt.MaxWorkers < 1 || opt.TimeoutSeconds < 1) throw new ArgumentException("Workers and timeout must be positive.");
        if (!Enum.IsDefined(opt.ExecutionMode)) throw new ArgumentException("Execution mode must be Auto, Isolated, or Batched.");
        string app = Path.GetFullPath(opt.ProjectPath);
        var tests = opt.TestProjects.Select(Path.GetFullPath).ToArray();
        if (tests.Length == 0) throw new ArgumentException("At least one NUnit/VSTest project is required.");
        if (!File.Exists(app) || tests.Any(t => !File.Exists(t))) throw new FileNotFoundException("Project or test project does not exist.");
        var output = Path.GetFullPath(opt.OutputDirectory); Directory.CreateDirectory(output);
        var sw = Stopwatch.StartNew(); var diagnostics = new List<string>();
        var compileRejected = new List<MutationResult>();
        var metrics = new RunMetrics();
        long discoveryMs = 0, buildMs = 0, baselineMs = 0, coverageMs = 0, generationMs = 0, evaluationMs = 0, copyMs = 0, instrumentationMs = 0, planningMs = 0, workerInitializationMs = 0, executionMs = 0, reportingMs = 0;
        string session = Path.Combine(Path.GetTempPath(), "mutation-framework", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session);
        try
        {
            var timer = Stopwatch.StartNew();
            var sourceFiles = await GetCompileFiles(app, metrics, ct);
            evaluationMs = timer.ElapsedMilliseconds;
            var candidates = DiscoverCandidates(sourceFiles, app, opt, diagnostics, ct);
            generationMs = timer.ElapsedMilliseconds - evaluationMs;
            var appCopy = Path.Combine(session, "app");
            timer.Restart();
            CopyProject(Path.GetDirectoryName(app)!, appCopy);
            var clonedApp = Path.Combine(appCopy, Path.GetFileName(app));
            var clonedTests = new List<string>();
            for (int i = 0; i < tests.Length; i++)
            {
                var testCopy = Path.Combine(session, "tests", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                CopyProject(Path.GetDirectoryName(tests[i])!, testCopy);
                var clonedTest = Path.Combine(testCopy, Path.GetFileName(tests[i]));
                RewireProjectReference(clonedTest, appCopy, Path.GetFileName(app));
                clonedTests.Add(clonedTest);
            }
            copyMs = timer.ElapsedMilliseconds;
            timer.Restart();
            var mappedFiles = sourceFiles.ToDictionary(f => f, f => Path.Combine(appCopy, Path.GetRelativePath(Path.GetDirectoryName(app)!, f)), StringComparer.OrdinalIgnoreCase);
            RewriteSources(mappedFiles, candidates, Path.GetDirectoryName(app)!);
            File.WriteAllText(Path.Combine(appCopy, "MutationRuntime.g.cs"), RuntimeSource(candidates.Select(c => c.SiteRuntimeId).Distinct(StringComparer.Ordinal).Count()));
            instrumentationMs = timer.ElapsedMilliseconds;
            timer.Restart();
            var testDlls = new List<string>();
            long recoveryInstrumentationMs = 0;
            int recoveryPasses = 0;
            while (true)
            {
                testDlls.Clear();
                bool retryBuild = false;
                foreach (var clonedTest in clonedTests)
                {
                    var build = await RunProcess("dotnet", ["build", clonedTest, "-c", "Debug", "--nologo", "-v:q"], session, null, opt.TimeoutSeconds, metrics, ct);
                    if (build.ExitCode != 0)
                    {
                        var invalidCandidates = FindInvalidCandidates(build.Output, candidates, appCopy);
                        if (invalidCandidates.Count == 0 || recoveryPasses >= 2)
                            throw new InvalidOperationException("Mutation-enabled project build failed without a safely attributable candidate, or the bounded recovery limit was reached. " + build.Output);
                        foreach (var failure in invalidCandidates)
                        {
                            compileRejected.Add(ToResult(failure.Candidate, MutationStatus.CompileError, [], [], 0, failure.Diagnostic));
                            diagnostics.Add($"CompileError: {failure.Candidate.Id} at {failure.Candidate.File}:{failure.Candidate.Line}: {failure.Diagnostic}");
                        }
                        var rejectedIds = invalidCandidates.Select(failure => failure.Candidate.Id).ToHashSet(StringComparer.Ordinal);
                        candidates.RemoveAll(candidate => rejectedIds.Contains(candidate.Id));
                        ReindexCandidates(candidates);
                        var recoveryTimer = Stopwatch.StartNew();
                        foreach (var pair in mappedFiles) File.Copy(pair.Key, pair.Value, true);
                        RewriteSources(mappedFiles, candidates, Path.GetDirectoryName(app)!);
                        File.WriteAllText(Path.Combine(appCopy, "MutationRuntime.g.cs"), RuntimeSource(candidates.Select(c => c.SiteRuntimeId).Distinct(StringComparer.Ordinal).Count()));
                        recoveryTimer.Stop(); recoveryInstrumentationMs += recoveryTimer.ElapsedMilliseconds;
                        recoveryPasses++;
                        retryBuild = true;
                        break;
                    }
                    testDlls.Add(FindTestAssembly(clonedTest));
                }
                if (!retryBuild) break;
            }
            buildMs = timer.ElapsedMilliseconds - recoveryInstrumentationMs;
            instrumentationMs += recoveryInstrumentationMs;
            timer.Restart();
            using var baselineSession = new TestPlatformSession(TestPlatformPaths.FindConsole(clonedTests[0]), metrics.StartedTestRunner);
            metrics.DiscoveryOperations++;
            var stableTestSources = testDlls.Select((assembly, index) =>
            {
                string relativeAssemblyPath = Path.GetRelativePath(Path.GetDirectoryName(clonedTests[index])!, assembly);
                string stableAssemblyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(tests[index])!, relativeAssemblyPath));
                return (Runtime: Path.GetFullPath(assembly), Stable: stableAssemblyPath);
            }).ToDictionary(pair => pair.Runtime, pair => pair.Stable, StringComparer.OrdinalIgnoreCase);
            var discoveredTestCases = baselineSession.Discover(testDlls, ct, stableTestSources);
            metrics.StartedTestHost(testDlls.Count);
            var testCases = discoveredTestCases
                .Where(t => opt.TestFilter == null || t.FullyQualifiedName.Contains(opt.TestFilter, StringComparison.OrdinalIgnoreCase) || t.DisplayName.Contains(opt.TestFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
            discoveryMs = timer.ElapsedMilliseconds;
            if (testCases.Length == 0) throw new InvalidOperationException("Test discovery failed or returned no tests.");
            var testToSites = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var baselineByName = new Dictionary<string, TestRun>(StringComparer.Ordinal);
            var coverageStatuses = new SortedDictionary<string, CoverageStatus>(StringComparer.Ordinal);
            long coverageElapsed = 0, probeSerializationMs = 0, coverageMappingMs = 0;
            timer.Restart();
            foreach (var testCase in testCases)
            {
                ct.ThrowIfCancellationRequested();
                metrics.BaselineTests++;
                var run = await ExecuteTest(testCase, session, opt, null, baselineSession, metrics, ct);
                baselineByName[testCase.Id] = run;
                var coverageTimer = Stopwatch.StartNew();
                ProbeRead coverage;
                try { coverage = ReadProbeSites(run.ProbePath, candidates); }
                catch (CoverageCollectionException ex)
                {
                    coverageStatuses[testCase.Id] = ex.Status;
                    diagnostics.Add(ex.Message);
                    throw new BaselineFailureException("Coverage collection failed; mutation execution cannot continue.", diagnostics);
                }
                coverageTimer.Stop(); coverageMappingMs += coverageTimer.ElapsedMilliseconds;
                probeSerializationMs += coverage.SerializationMs;
                coverageElapsed += coverageTimer.ElapsedMilliseconds + coverage.SerializationMs;
                testToSites[testCase.Id] = coverage.Sites;
                coverageStatuses[testCase.Id] = coverage.Status;
                if (!run.Passed && !run.Skipped) diagnostics.Add($"Baseline failed: {testCase.DisplayName}: {run.Error}");
            }
            baselineMs = timer.ElapsedMilliseconds - coverageElapsed; coverageMs = coverageElapsed;
            if (baselineByName.Values.Any(x => !x.Passed && !x.Skipped)) throw new BaselineFailureException("Baseline test failures prevent mutation execution.", diagnostics);
            var testsById = testCases.ToDictionary(t => t.Id, StringComparer.Ordinal);
            var siteTests = new Dictionary<string, List<TestCaseDescriptor>>(StringComparer.Ordinal);
            foreach (var pair in testToSites) foreach (var site in pair.Value) { if (!siteTests.TryGetValue(site, out var list)) siteTests[site] = list = []; list.Add(testsById[pair.Key]); }
            var preclassified = new List<MutationResult>(compileRejected);
            var workItems = new List<MutationWorkItem>();
            foreach (var candidate in candidates)
            {
                var relevant = siteTests.GetValueOrDefault(candidate.SiteId) ?? [];
                if (relevant.Count == 0)
                    preclassified.Add(ToResult(candidate, MutationStatus.NoCoverage, [], [], 0));
                else
                    workItems.Add(new(candidate.Id, relevant.Select(t => t.Id).Order(StringComparer.Ordinal).ToArray(),
                        relevant.Sum(t => baselineByName[t.Id].DurationMs)));
            }
            var executionPlan = MutationPlanner.Build(workItems, preclassified, opt.ExecutionMode, coverageComplete: true, opt.TimeoutSeconds, opt.MaxWorkers);
            planningMs = timer.ElapsedMilliseconds - coverageElapsed - baselineMs;
            var completed = new MutationResult[candidates.Count + compileRejected.Count];
            var candidateIndexById = candidates.Select((candidate, index) => (candidate.Id, index)).ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal);
            for (int rejectedIndex = 0; rejectedIndex < compileRejected.Count; rejectedIndex++)
                completed[candidates.Count + rejectedIndex] = compileRejected[rejectedIndex];
            bool runCanceled = false;
            timer.Restart();
            var workerBusy = new long[executeMutants ? Math.Min(opt.MaxWorkers, executionPlan.Batches.Count) : 0];
            if (!executeMutants)
            {
                foreach (var classifiedResult in executionPlan.PreclassifiedMutants) completed[candidateIndexById[classifiedResult.Id]] = classifiedResult;
                foreach (var item in workItems)
                {
                    var c = candidates[candidateIndexById[item.MutantId]];
                    completed[candidateIndexById[item.MutantId]] = ToResult(c, MutationStatus.Pending,
                        item.TestIds.Select(id => testsById[id].DisplayName).ToArray(), [], 0);
                }
            }
            else
            {
                foreach (var classifiedResult in executionPlan.PreclassifiedMutants) completed[candidateIndexById[classifiedResult.Id]] = classifiedResult;
                int nextBatch = -1;
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
                            int batchIndex = Interlocked.Increment(ref nextBatch);
                            if (batchIndex >= executionPlan.Batches.Count) { busy.Stop(); workerBusy[workerIndex] = busy.ElapsedMilliseconds; return; }
                            ct.ThrowIfCancellationRequested();
                            var batch = executionPlan.Batches[batchIndex];
                            foreach (var mutantId in batch.MutantIds)
                            {
                                ct.ThrowIfCancellationRequested();
                                int i = candidateIndexById[mutantId]; var c = candidates[i];
                                var relevant = batch.TestToMutantId.Where(pair => pair.Value == mutantId).Select(pair => testsById[pair.Key]).OrderBy(test => test.Id, StringComparer.Ordinal).ToArray();
                                var kill = new List<string>(); long duration = 0; var status = MutationStatus.Survived; string? error = null;
                                foreach (var testCase in relevant)
                                {
                                    Interlocked.Increment(ref metrics.MutationTests);
                                    var run = await ExecuteTest(testCase, session, opt, c.MutantRuntimeId, workerSession, metrics, ct); duration += run.DurationMs;
                                    if (run.Skipped) { status = MutationStatus.Inconclusive; error = "Selected test was skipped."; break; }
                                    if (!run.Passed)
                                    {
                                        if (run.Error != null && run.Error.StartsWith("TEST_FAILURE:", StringComparison.Ordinal)) { status = MutationStatus.Killed; error = run.Error; kill.Add(testCase.DisplayName); break; }
                                        if (run.Error != null && run.Error.StartsWith("TEST_FAILURE_UNATTRIBUTED:", StringComparison.Ordinal)) { status = MutationStatus.Inconclusive; error = run.Error; break; }
                                        status = run.Error?.StartsWith("TIMEOUT:", StringComparison.Ordinal) == true ? MutationStatus.Timeout : MutationStatus.InfrastructureError; error = run.Error; break;
                                    }
                                }
                                completed[i] = ToResult(c, status, relevant.Select(test => test.DisplayName).ToArray(), kill, duration, error);
                                if (status is MutationStatus.InfrastructureError or MutationStatus.Timeout)
                                {
                                    if (status == MutationStatus.InfrastructureError) Interlocked.Increment(ref metrics.WorkerFailures);
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
                    for (int i = 0; i < candidates.Count; i++)
                        completed[i] ??= ToResult(candidates[i], MutationStatus.Inconclusive, [], [], 0, "Run canceled before this mutant completed.");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref metrics.WorkerFailures);
                    diagnostics.Add("Mutation worker failed: " + ex.Message);
                    for (int i = 0; i < candidates.Count; i++)
                        completed[i] ??= ToResult(candidates[i], MutationStatus.InfrastructureError, [], [], 0, "Worker failed before this mutant completed: " + ex.Message);
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
            int coveredCount = workItems.Count;
            var counters = new ExecutionCounters(ordered.Length, coveredCount, uncovered, testCases.Length, metrics.BaselineTests, metrics.MutationTests, metrics.TestRunnerStarts, metrics.DiscoveryOperations, metrics.BuildInvocations, metrics.WorkerRestarts,
                metrics.WorkerFailures, executionPlan.Batches.Count, executionPlan.Statistics.AverageMutantsPerBatch, busyMs,
                Math.Max(0, executionMs * workerBusy.Length - busyMs)) { TestHostProcessStarts = metrics.TestHostStarts };
            string overallStatus = !executeMutants ? "Planned" : runCanceled ? "Canceled" : ordered.Any(x => x.Status is MutationStatus.InfrastructureError or MutationStatus.Timeout or MutationStatus.Inconclusive or MutationStatus.CompileError) ? "CompletedWithErrors" : "Completed";
            var result = new MutationRunResult(overallStatus, ordered, killed, survived, uncovered, denominator == 0 ? null : (double)killed * 100 / denominator, timings, sw.ElapsedMilliseconds, testToSites, diagnostics, Path.Combine(output, "mutation-report.json"), Path.Combine(output, "index.html"))
            {
                Counters = counters,
                ProcessStarts = metrics.ProcessStarts,
                ExecutionPlan = executionPlan,
                Tests = testCases.Select(t => new TestIdentity(t.Id, t.FullyQualifiedName, t.DisplayName, baselineByName[t.Id].DurationMs, t.ExecutorUri, t.SourceAssembly, t.Case.Id.ToString("N"))).ToArray(),
                CoverageStatuses = coverageStatuses,
            };
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true }; jsonOptions.Converters.Add(new JsonStringEnumConverter());
            if (opt.Report.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("json", StringComparer.OrdinalIgnoreCase)) await File.WriteAllTextAsync(result.JsonReportPath, JsonSerializer.Serialize(result, jsonOptions), ct);
            if (opt.Report.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("html", StringComparer.OrdinalIgnoreCase)) await File.WriteAllTextAsync(result.HtmlReportPath, Html(result), ct);
            reportingMs = timer.ElapsedMilliseconds;
            result = result with { Timings = timings with { ReportingMs = reportingMs }, TotalDurationMs = sw.ElapsedMilliseconds };
            if (File.Exists(result.JsonReportPath)) await File.WriteAllTextAsync(result.JsonReportPath, JsonSerializer.Serialize(result, jsonOptions), ct);
            if (File.Exists(result.HtmlReportPath)) await File.WriteAllTextAsync(result.HtmlReportPath, Html(result), ct);
            Console.WriteLine($"Mutants: {ordered.Length}; killed: {killed}; survived: {survived}; no coverage: {result.NoCoverageCount}; score: {(result.MutationScore is null ? "n/a" : result.MutationScore.Value.ToString("F1") + "%")}");
            return result;
        }
        finally { try { Directory.Delete(session, true); } catch { } }
    }

    private static MutationResult ToResult(Candidate c, MutationStatus s, IReadOnlyList<string> tests, IReadOnlyList<string> killing, long ms, string? error = null) => new(c.Id, c.SiteId, c.Operator, c.File, c.Line, c.Column, c.Original, c.Replacement, s, tests, killing, ms, error);

    private static async Task<TestRun> ExecuteTest(TestCaseDescriptor test, string session, MutationOptions opt, string? active, TestPlatformSession platform, RunMetrics metrics, CancellationToken ct)
    {
        string resultDir = Path.Combine(session, "results", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(resultDir);
        string probe = Path.Combine(resultDir, "probes.json");
        var timer = Stopwatch.StartNew();
        var result = await Task.Run(() => platform.Execute(test, probe, active, opt.TimeoutSeconds, () => metrics.StartedTestHost(), ct), CancellationToken.None);
        timer.Stop();
        return new(test.DisplayName, result.Passed, result.Skipped, timer.ElapsedMilliseconds, result.Error) { ProbePath = probe };
    }

    private static async Task<Proc> RunProcess(string exe, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string?>? env, int timeout, RunMetrics metrics, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true }; p.Start(); metrics.StartedProcess(exe, args); var stdout = p.StandardOutput.ReadToEndAsync(ct); var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct); timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout)); bool timed = false;
        try { await p.WaitForExitAsync(timeoutCts.Token); } catch (OperationCanceledException) { timed = !ct.IsCancellationRequested; try { p.Kill(true); } catch { } if (ct.IsCancellationRequested) throw; }
        var output = await stdout + await stderr; return new(timed ? -1 : p.ExitCode, output, timed);
    }
    private sealed record Proc(int ExitCode, string Output, bool TimedOut);

    private static async Task<string[]> GetCompileFiles(string project, RunMetrics metrics, CancellationToken ct)
    {
        var p = await RunProcess("dotnet", ["msbuild", project, "-getItem:Compile", "-nologo"], Path.GetDirectoryName(Path.GetFullPath(project))!, null, 60, metrics, ct);
        if (p.ExitCode != 0) throw new InvalidOperationException("Unable to evaluate project Compile items: " + p.Output);
        using var doc = JsonDocument.Parse(p.Output); return doc.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray().Select(e => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, e.GetProperty("Identity").GetString()!))).Where(File.Exists).Where(f => !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) && !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string FindTestAssembly(string project)
    {
        var xml = XDocument.Load(project);
        string? framework = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(framework)) framework = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(framework)) throw new InvalidOperationException("Unable to determine the test project's target framework.");
        string assembly = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, "bin", "Debug", framework, Path.GetFileNameWithoutExtension(project) + ".dll");
        return File.Exists(assembly) ? assembly : throw new FileNotFoundException("Built test assembly not found.", assembly);
    }

    internal static List<Candidate> DiscoverCandidates(string[] files, string project, MutationOptions opt, List<string> diagnostics, CancellationToken ct)
    {
        var list = new List<Candidate>(); var root = Path.GetDirectoryName(Path.GetFullPath(project))!;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = files.Where(f => !Path.GetFileName(f).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => (File: f, Tree: CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f, options: parseOptions))).ToList();
        string? globalUsings = Path.Combine(root, "obj", "Debug", "net11.0", Path.GetFileNameWithoutExtension(project) + ".GlobalUsings.g.cs");
        if (File.Exists(globalUsings)) trees.Add((globalUsings, CSharpSyntaxTree.ParseText(File.ReadAllText(globalUsings), path: globalUsings, options: parseOptions)));
        var referencePaths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        var references = referencePaths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var compilation = CSharpCompilation.Create("MutagenSemanticValidation", trees.Select(x => x.Tree), references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var treeByFile = trees.ToDictionary(x => x.File, x => x.Tree, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested(); var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (Path.GetFileName(file).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (opt.Include != null && !rel.Contains(opt.Include, StringComparison.OrdinalIgnoreCase) || opt.Exclude != null && rel.Contains(opt.Exclude, StringComparison.OrdinalIgnoreCase)) continue;
            var tree = treeByFile[file]; var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                var replacements = node.Kind() switch
                {
                    SyntaxKind.AddExpression => new[] { ("Arithmetic", SyntaxKind.SubtractExpression) },
                    SyntaxKind.SubtractExpression => new[] { ("Arithmetic", SyntaxKind.AddExpression) },
                    SyntaxKind.MultiplyExpression => new[] { ("Arithmetic", SyntaxKind.DivideExpression) },
                    SyntaxKind.GreaterThanExpression => new[] { ("Equality", SyntaxKind.GreaterThanOrEqualExpression), ("Equality", SyntaxKind.LessThanExpression) },
                    SyntaxKind.GreaterThanOrEqualExpression => new[] { ("Equality", SyntaxKind.GreaterThanExpression) },
                    SyntaxKind.LessThanExpression => new[] { ("Equality", SyntaxKind.LessThanOrEqualExpression) },
                    SyntaxKind.EqualsExpression => new[] { ("Equality", SyntaxKind.NotEqualsExpression) },
                    SyntaxKind.NotEqualsExpression => new[] { ("Equality", SyntaxKind.EqualsExpression) },
                    SyntaxKind.LogicalAndExpression => new[] { ("Logical", SyntaxKind.LogicalOrExpression) },
                    SyntaxKind.LogicalOrExpression => new[] { ("Logical", SyntaxKind.LogicalAndExpression) },
                    _ => []
                };
                if (replacements.Length > 0 && !IsSupportedMutationSite(node, model, out var reason))
                {
                    diagnostics.Add($"Unsupported mutation site {rel}:{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: {reason}");
                    continue;
                }
                foreach (var (op, kind) in replacements)
                {
                    string replacementToken = TokenFor(kind), original = node.ToString();
                    string id = Hash(rel + "|" + node.SpanStart + "|" + replacementToken); string site = Hash(rel + "|" + node.SpanStart + "|" + node.Span.Length);
                    var line = tree.GetLineSpan(node.Span).StartLinePosition;
                    list.Add(new(id, site, op, rel, node.SpanStart, node.Span.Length, line.Line + 1, line.Character + 1, original, ReplaceOperator(node, kind), replacementToken, "0", "0"));
                }
            }
            foreach (var lit in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Where(x => x.IsKind(SyntaxKind.TrueLiteralExpression) || x.IsKind(SyntaxKind.FalseLiteralExpression)))
            {
                if (!IsSupportedMutationSite(lit, model, out var reason)) { diagnostics.Add($"Unsupported mutation site {rel}:{tree.GetLineSpan(lit.Span).StartLinePosition.Line + 1}: {reason}"); continue; }
                var replacement = lit.IsKind(SyntaxKind.TrueLiteralExpression) ? "false" : "true"; string id = Hash(rel + "|" + lit.SpanStart + "|" + replacement); string site = Hash(rel + "|" + lit.SpanStart + "|" + lit.Span.Length); var line = tree.GetLineSpan(lit.Span).StartLinePosition;
                list.Add(new(id, site, "Boolean", rel, lit.SpanStart, lit.Span.Length, line.Line + 1, line.Character + 1, lit.ToString(), replacement, replacement, "0", "0"));
            }
        }
        var unique = list.GroupBy(x => (x.File, x.Start, x.Length, x.ReplacementToken)).Select(x => x.First()).OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        var siteIndices = unique.Select(x => x.SiteId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index.ToString(), StringComparer.Ordinal);
        var mutantIndices = unique.Select(x => x.Id).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index.ToString(), StringComparer.Ordinal);
        return unique.Select(x => x with { SiteRuntimeId = siteIndices[x.SiteId], MutantRuntimeId = mutantIndices[x.Id] }).ToList();
    }
    private static bool IsSupportedMutationSite(ExpressionSyntax expression, SemanticModel model, out string reason)
    {
        var symbolInfo = model.GetSymbolInfo(expression);
        var operation = model.GetOperation(expression);
        if (symbolInfo.Symbol is IMethodSymbol method && method.MethodKind == MethodKind.UserDefinedOperator)
        { reason = "user-defined operators are excluded"; return false; }
        if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression) &&
            (model.GetTypeInfo(binary.Left).Type?.SpecialType == SpecialType.System_String || model.GetTypeInfo(binary.Right).Type?.SpecialType == SpecialType.System_String))
        { reason = "string concatenation is excluded"; return false; }
        foreach (var syntax in expression.DescendantNodesAndSelf())
        {
            if (syntax is AwaitExpressionSyntax) { reason = "await expressions cannot be wrapped without changing async semantics"; return false; }
            if (syntax is not ExpressionSyntax current) continue;
            var type = model.GetTypeInfo(current).Type;
            if (type?.TypeKind is TypeKind.Dynamic or TypeKind.Error) { reason = "dynamic or unresolved operand type"; return false; }
            if (type?.IsRefLikeType == true || type?.TypeKind == TypeKind.Pointer || type is ITypeParameterSymbol)
            { reason = "ref-like, pointer, or generic operand type"; return false; }
            if (current is IdentifierNameSyntax identifier && model.GetSymbolInfo(identifier).Symbol is ILocalSymbol local && (local.RefKind != RefKind.None || local.Type.IsRefLikeType))
            { reason = "ref or ref-like local capture"; return false; }
        }
        foreach (var lambda in expression.Ancestors().OfType<AnonymousFunctionExpressionSyntax>())
        {
            var converted = model.GetTypeInfo(lambda).ConvertedType;
            if (converted is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>")
            { reason = "expression-tree contexts are excluded"; return false; }
        }
        if (expression.Ancestors().Any(x => x is AttributeArgumentSyntax or CaseSwitchLabelSyntax or ConstantPatternSyntax) ||
            HasExplicitOverflowContext(expression) ||
            expression.Ancestors().OfType<ParameterSyntax>().Any(p => p.Default?.Value == expression) ||
            expression.Ancestors().OfType<VariableDeclaratorSyntax>().Any(v => v.Initializer?.Value == expression && v.Parent?.Parent is FieldDeclarationSyntax field && field.Modifiers.Any(SyntaxKind.ConstKeyword)) ||
            expression.Ancestors().OfType<LocalDeclarationStatementSyntax>().Any(d => d.Modifiers.Any(SyntaxKind.ConstKeyword) && d.Declaration.Variables.Any(v => v.Initializer?.Value == expression)))
        { reason = HasExplicitOverflowContext(expression) ? "explicit checked/unchecked arithmetic context cannot be preserved by expression instrumentation" : "constant-expression context"; return false; }
        if (operation == null) { reason = "Roslyn could not bind the expression"; return false; }
        reason = string.Empty;
        return true;
    }
    private static bool HasExplicitOverflowContext(ExpressionSyntax expression) => expression.Ancestors().Any(node =>
        node.IsKind(SyntaxKind.CheckedExpression) || node.IsKind(SyntaxKind.UncheckedExpression) ||
        node.IsKind(SyntaxKind.CheckedStatement) || node.IsKind(SyntaxKind.UncheckedStatement));
    private static string ReplaceOperator(BinaryExpressionSyntax n, SyntaxKind kind) => SyntaxFactory.ParseExpression($"({n.Left}) {TokenFor(kind)} ({n.Right})").ToString();
    private static string TokenFor(SyntaxKind k) => k switch { SyntaxKind.AddExpression => "+", SyntaxKind.SubtractExpression => "-", SyntaxKind.MultiplyExpression => "*", SyntaxKind.DivideExpression => "/", SyntaxKind.GreaterThanExpression => ">", SyntaxKind.GreaterThanOrEqualExpression => ">=", SyntaxKind.LessThanExpression => "<", SyntaxKind.LessThanOrEqualExpression => "<=", SyntaxKind.EqualsExpression => "==", SyntaxKind.NotEqualsExpression => "!=", SyntaxKind.LogicalAndExpression => "&&", SyntaxKind.LogicalOrExpression => "||", _ => throw new ArgumentOutOfRangeException(nameof(k)) };
    internal static void RewriteSources(Dictionary<string, string> files, List<Candidate> candidates, string projectRoot)
    {
        foreach (var (original, target) in files)
        {
            if (!File.Exists(target)) continue;
            var text = File.ReadAllText(target); var tree = CSharpSyntaxTree.ParseText(text, path: target, options: new CSharpParseOptions(LanguageVersion.Preview));
            var local = candidates.Where(c => string.Equals(c.File, Path.GetRelativePath(projectRoot, original).Replace('\\','/'), StringComparison.OrdinalIgnoreCase)).ToArray();
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
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
            if (node.Expression is null) return visited;
            var siteIds = candidates.Where(candidate => candidate.Start == node.Expression.SpanStart && candidate.Length == node.Expression.Span.Length)
                .Select(candidate => candidate.SiteRuntimeId).Where(statementProbeSites.Contains).Distinct(StringComparer.Ordinal).ToArray();
            if (siteIds.Length == 0) return visited;
            var hits = siteIds.Select(site => (StatementSyntax)SyntaxFactory.ParseStatement($"MutationRuntime.Hit({site});"));
            return SyntaxFactory.Block(hits.Append(visited)).WithTriviaFrom(node);
        }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            var ops = candidates.Where(c => c.Start == node.SpanStart && c.Length == node.Span.Length && c.Operator != "Boolean").ToArray();
            ExpressionSyntax result = visited;
            foreach (var c in ops) { var mut = SyntaxFactory.ParseExpression($"({visited.Left}) {c.ReplacementToken} ({visited.Right})"); result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({c.MutantRuntimeId})"), SyntaxFactory.ParenthesizedExpression(mut), SyntaxFactory.ParenthesizedExpression(result)); }
            // Record entry before evaluating the expression, including when it throws.
            if (ops.Length > 0 && !statementProbeSites.Contains(ops[0].SiteRuntimeId)) result = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"), SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(SyntaxFactory.ParseExpression(ops[0].SiteRuntimeId)), SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(result)) })));
            return result.WithTriviaFrom(node);
        }
        public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            var c = candidates.FirstOrDefault(x => x.Operator == "Boolean" && x.Start == node.SpanStart); if (c == null) return base.VisitLiteralExpression(node);
            var expr = (ExpressionSyntax)SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({c.MutantRuntimeId}) ? {c.Replacement} : {node}").WithTriviaFrom(node);
            if (statementProbeSites.Contains(c.SiteRuntimeId)) return expr.WithTriviaFrom(node);
            return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"), SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(SyntaxFactory.ParseExpression(c.SiteRuntimeId)), SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(expr)) }))).WithTriviaFrom(node);
        }
    }

    private static string RuntimeSource(int siteCount) => $"using System; using System.IO; using System.Text.Json; using System.Diagnostics; using System.Linq; using System.Threading; using System.Runtime.CompilerServices; internal static class MutationRuntime {{ static readonly int Active = int.TryParse(Environment.GetEnvironmentVariable(\"MUTATION_ACTIVE_ID\"), out var id) ? id : -1; static readonly int[] Hits = new int[{siteCount}]; [ModuleInitializer] internal static void Initialize() {{ _ = Hits.Length; }} static MutationRuntime() {{ AppDomain.CurrentDomain.ProcessExit += (_,__) => {{ var p=Environment.GetEnvironmentVariable(\"MUTATION_PROBE_FILE\"); if(!string.IsNullOrEmpty(p)) {{ var tmp=p+\".tmp\"; var sw=Stopwatch.StartNew(); var ids=JsonSerializer.Serialize(Enumerable.Range(0,Hits.Length).Where(i=>Volatile.Read(ref Hits[i])!=0)); sw.Stop(); var payload=\"{{\\\"siteIds\\\":\"+ids+\",\\\"serializationMs\\\":\"+sw.ElapsedMilliseconds+\"}}\"; File.WriteAllText(tmp,payload); File.Move(tmp,p,true); }} }}; }} public static bool IsActive(int id) => Active == id; public static void Hit(int id) => Interlocked.Exchange(ref Hits[id],1); public static T Probe<T>(int id, Func<T> value) {{ Hit(id); return value(); }} }}";

    internal sealed record ProbeRead(string[] Sites, long SerializationMs, CoverageStatus Status = CoverageStatus.CoverageSuccess);
    internal static ProbeRead ReadProbeSites(string path, List<Candidate> c)
    {
        if (!File.Exists(path)) throw new CoverageCollectionException(CoverageStatus.CoverageUnavailable, $"CoverageUnavailable: probe artifact was not produced: {path}");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            int[] values; long serializationMs;
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                values = document.RootElement.Deserialize<int[]>() ?? throw new JsonException("Probe artifact contains null.");
                serializationMs = 0;
            }
            else
            {
                values = document.RootElement.GetProperty("siteIds").Deserialize<int[]>() ?? throw new JsonException("Probe artifact siteIds contains null.");
                serializationMs = document.RootElement.GetProperty("serializationMs").GetInt64();
                if (serializationMs < 0) throw new JsonException("Probe artifact has a negative serialization duration.");
            }
            var ids = values.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
            return new(c.Where(x => ids.Contains(x.SiteRuntimeId)).Select(x => x.SiteId).Distinct(StringComparer.Ordinal).ToArray(), serializationMs);
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
        var result = new List<string>(); bool reading = false;
        foreach (var raw in text.Split('\n')) { var line = raw.Trim(); if (line.StartsWith("The following Tests are available:", StringComparison.OrdinalIgnoreCase)) { reading = true; continue; } if (reading && line.Length > 0 && !line.StartsWith("Test run", StringComparison.OrdinalIgnoreCase)) result.Add(line.TrimEnd('\r')); }
        return result.ToArray();
    }
    internal static List<(Candidate Candidate, string Diagnostic)> FindInvalidCandidates(string buildOutput, IReadOnlyList<Candidate> candidates, string appCopy)
    {
        const string diagnosticPattern = @"^(?<path>.+?)\((?<line>\d+),(?<column>\d+)\): error (?<code>CS\d+): (?<message>.*)$";
        var failures = new Dictionary<string, (Candidate Candidate, string Diagnostic)>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(buildOutput, diagnosticPattern, RegexOptions.Multiline))
        {
            string file = Path.GetFullPath(match.Groups["path"].Value.Trim());
            int line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            string diagnostic = $"{match.Groups["code"].Value}: {match.Groups["message"].Value.Trim()}";
            foreach (var candidate in candidates.Where(candidate => candidate.Line == line &&
                         string.Equals(Path.GetFullPath(Path.Combine(appCopy, candidate.File)), file, StringComparison.OrdinalIgnoreCase)))
                failures.TryAdd(candidate.Id, (candidate, diagnostic));
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
        for (int index = 0; index < candidates.Count; index++)
            candidates[index] = candidates[index] with { SiteRuntimeId = siteIndices[candidates[index].SiteId], MutantRuntimeId = mutationIndices[candidates[index].Id] };
    }
    private static void CopyProject(string from, string to) { Directory.CreateDirectory(to); foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories).Where(d => d.Split(Path.DirectorySeparatorChar).All(x => !string.Equals(x, "bin", StringComparison.OrdinalIgnoreCase) && !string.Equals(x, "obj", StringComparison.OrdinalIgnoreCase)))) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir))); foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories).Where(f => f.Split(Path.DirectorySeparatorChar).All(x => !string.Equals(x, "bin", StringComparison.OrdinalIgnoreCase) && !string.Equals(x, "obj", StringComparison.OrdinalIgnoreCase)))) { var dest = Path.Combine(to, Path.GetRelativePath(from, file)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest, true); } }
    private static void RewireProjectReference(string testProject, string appDir, string appProjectName) { var x = XDocument.Load(testProject); var refs = x.Descendants().Where(e => e.Name.LocalName == "ProjectReference").ToArray(); foreach (var r in refs) { var include = (string?)r.Attribute("Include"); if (include != null && Path.GetFileName(include).Equals(appProjectName, StringComparison.OrdinalIgnoreCase)) r.SetAttributeValue("Include", Path.Combine(appDir, appProjectName)); } x.Save(testProject); }
    private static string Html(MutationRunResult r) { static string E(string s) => System.Net.WebUtility.HtmlEncode(s); var t = r.Timings; var c = r.Counters; var b = new StringBuilder("<!doctype html><meta charset=utf-8><title>Mutation report</title><style>body{font:15px system-ui;max-width:1100px;margin:2rem auto}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:.5rem;text-align:left}code{white-space:pre-wrap}.Killed{color:#a00}.Survived{color:#087}</style>"); b.Append($"<h1>Mutation report</h1><p>Status: {E(r.OverallStatus)} | Score: {(r.MutationScore?.ToString("F1") + "%" ?? "n/a")} | Total: {r.Mutants.Count} | Killed: {r.KilledCount} | Survived: {r.SurvivedCount} | No coverage: {r.NoCoverageCount} | Total duration: {r.TotalDurationMs} ms</p><h2>Exclusive phase timings (ms)</h2><p>Project evaluation {t.ProjectEvaluationMs}; source discovery {t.DiscoveryMs}; source copy {t.ProjectCopyMs}; Roslyn/instrumentation {t.InstrumentationMs}; build {t.BuildMs}; baseline tests {t.BaselineMs}; probe serialization {t.ProbeSerializationMs}; coverage mapping {t.CoverageMappingMs}; mutation generation {t.MutationGenerationMs}; planning {t.MutationPlanningMs}; worker initialization {t.WorkerInitializationMs}; mutation execution {Math.Max(0, t.ExecutionMs - t.WorkerInitializationMs)}; reporting {t.ReportingMs}.</p><p>Execution mode: {E(r.ExecutionPlan?.SelectedMode.ToString() ?? "not executed")}; planned batches: {r.ExecutionPlan?.Batches.Count ?? 0}; conflict edges: {r.ExecutionPlan?.Statistics.ConflictEdges ?? 0}.</p><h2>Execution counters</h2><p>Mutants: {c.TotalMutants} total, {c.CoveredMutants} covered, {c.UncoveredMutants} uncovered; tests {c.TotalTests}; baseline executions {c.BaselineTestExecutions}; mutation executions {c.MutationTestExecutions}; controller/test runner starts {c.TestRunnerProcessStarts}; testhost starts {c.TestHostProcessStarts}; discovery operations {c.TestDiscoveryOperations}; builds {c.BuildInvocations}; worker restarts {c.WorkerRestarts}; worker failures {c.WorkerFailures}; execution units {c.MutationBatches}, average mutants/unit {c.AverageMutantsPerBatch:F2}; worker busy {c.TotalWorkerBusyTimeMs} ms, idle {c.TotalWorkerIdleTimeMs} ms; observed process starts {r.ProcessStarts}.</p><table><thead><tr><th>Status</th><th>Source</th><th>Operator</th><th>Transformation</th><th>Tests</th></tr></thead><tbody>"); foreach (var m in r.Mutants) b.Append($"<tr><td class='{m.Status}'>{m.Status}</td><td>{E(m.SourceFile)}:{m.Line}:{m.Column}</td><td>{E(m.Operator)}</td><td><code>{E(m.Original)} &#8594; {E(m.Replacement)}</code></td><td>{E(string.Join(", ", m.CoveringTests))}</td></tr>"); return b.Append("</tbody></table>").ToString(); }
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
internal sealed class BaselineFailureException(string message, IReadOnlyList<string> errors) : Exception(message) { public IReadOnlyList<string> Errors { get; } = errors; }
internal sealed class CoverageCollectionException(CoverageStatus status, string message, Exception? inner = null) : Exception(message, inner)
{
    public CoverageStatus Status { get; } = status;
}
