using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed record TestRun(string Name, TestFailureKind FailureKind, long DurationMs, string? Error)
{
  public bool Passed => FailureKind == TestFailureKind.None;
  public bool Skipped => FailureKind is TestFailureKind.Skipped or TestFailureKind.Bailed;
  public string ProbePath { get; init; } = "";
  public string[] CoveredRuntimeSiteIds { get; init; } = [];
  public string? CoverageError { get; init; }
}


internal static class TestExecutor
{
  internal static async Task<TestRun> ExecuteTest(TestCaseDescriptor test, string session, MutationOptions opt, string? active, TestPlatformSession platform, ResourceLockManager locks, RunMetrics metrics, CancellationToken ct)
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
    return new(test.DisplayName, result.FailureKind, timer.ElapsedMilliseconds, result.Error) { ProbePath = probe ?? string.Empty };
  }

  internal static async Task<IReadOnlyList<TestRun>> ExecuteTestsInSharedMutationHost(TestCaseDescriptor[] tests,
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
      return new TestRun(test.DisplayName, result.FailureKind, result.DurationMs, result.Error);
    }).ToArray();
  }

  internal static async Task<IReadOnlyDictionary<string, TestRun>> ExecuteTestsWithPerTestCoverage(TestCaseDescriptor[] tests,
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
      return new TestRun(test.DisplayName, result.FailureKind, result.DurationMs, result.Error)
      {
        CoveredRuntimeSiteIds = result.CoveredRuntimeSiteIds ?? [],
        CoverageError = result.CoverageError
      };
    }, StringComparer.Ordinal);
  }

  internal static async Task<IReadOnlyList<MutationResult>> ExecuteBatchAsync(MutationBatch batch,
      Func<string, Candidate> resolveCandidate, IReadOnlyDictionary<string, TestCaseDescriptor> testsById,
      TestPlatformSession platform, MutationExecutionContext context, CancellationToken ct)
  {

    var opt = context.Options;
    var metrics = context.Metrics;
    var resourceLockManager = context.ResourceLocks;
    var batchTests = batch.TestIds.Select(testId => testsById[testId]).OrderBy(testCase => testCase.Id, StringComparer.Ordinal).ToArray();
    var activeMutationIds = string.Join(",", batch.MutantIds.Select(mutantId =>
        resolveCandidate(mutantId).MutantRuntimeId).Order(StringComparer.Ordinal));
    var locks = batch.TestIds.SelectMany(testId => opt.ResourceLocksByTestId.GetValueOrDefault(testId, []))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    Interlocked.Add(ref metrics.MutationTests, batchTests.Length);
    var mixedRuns = await resourceLockManager.ExecuteAsync(locks,
        () => TestExecutor.ExecuteTestsInSharedMutationHost(batchTests, opt, activeMutationIds, platform, metrics, ct, serializeTests: true), ct);
    var runsByTestId = batchTests.Zip(mixedRuns).ToDictionary(pair => pair.First.Id, pair => pair.Second, StringComparer.Ordinal);
    var results = new List<MutationResult>(batch.MutantIds.Count);
    foreach (var mutantId in batch.MutantIds)
    {
      var candidate = resolveCandidate(mutantId);
      var relevant = batch.TestToMutantId.Where(pair => pair.Value == mutantId)
          .Select(pair => (Test: testsById[pair.Key], Run: runsByTestId[pair.Key])).ToArray();
      var outcome = MutationOutcomeClassifier.ClassifyBatch(relevant.Select(pair => pair.Run).ToArray());
      results.Add(MutationResults.ToResult(candidate, outcome.Status,
          relevant.Select(pair => pair.Test.DisplayName).ToArray(), outcome.KillingTests, outcome.DurationMs, outcome.Diagnostic));
    }
    return results;
  }

  internal static async Task<MutationResult> ExecuteMutantAsync(Candidate candidate, TestCaseDescriptor[] relevant,
      TestPlatformSession platform, MutationExecutionContext context, bool sharedHost, CancellationToken ct)
  {

    var opt = context.Options;
    var metrics = context.Metrics;
    var resourceLockManager = context.ResourceLocks;
    var session = context.SessionDirectory;
    var kill = new List<string>(); long duration = 0; var status = MutationStatus.Survived; string? error = null;
    if (sharedHost)
    {
      var resourceLocks = relevant.SelectMany(testCase => opt.ResourceLocksByTestId.GetValueOrDefault(testCase.Id, []))
          .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
      var runs = await resourceLockManager.ExecuteAsync(resourceLocks,
          () => TestExecutor.ExecuteTestsInSharedMutationHost(relevant, opt, candidate.MutantRuntimeId, platform, metrics, ct,
              serializeTests: resourceLocks.Length != 0, stopAfterAttributableFailure: true), ct);
      Interlocked.Add(ref metrics.MutationTests,
          runs.Count(run => run.FailureKind != TestFailureKind.Bailed));
      var outcome = MutationOutcomeClassifier.ClassifyMutant(runs);
      duration = outcome.DurationMs;
      status = outcome.Status;
      error = outcome.Diagnostic;
      kill.AddRange(outcome.KillingTests);
    }
    else
    {
      foreach (var testCase in relevant)
      {
        Interlocked.Increment(ref metrics.MutationTests);
        var run = await TestExecutor.ExecuteTest(testCase, session, opt, candidate.MutantRuntimeId, platform, resourceLockManager, metrics, ct); duration += run.DurationMs;
        if (run.Skipped) { status = MutationStatus.Inconclusive; error = "Selected test was skipped."; break; }
        if (!run.Passed)
        {
          if (run.FailureKind == TestFailureKind.TestBody) { status = MutationStatus.Killed; error = run.Error; kill.Add(testCase.DisplayName); break; }
          if (run.FailureKind == TestFailureKind.Unattributed) { status = MutationStatus.Inconclusive; error = run.Error; break; }
          status = run.FailureKind == TestFailureKind.Timeout ? MutationStatus.Timeout : MutationStatus.InfrastructureError; error = run.Error; break;
        }
      }
    }
    var mutationResult = MutationResults.ToResult(candidate, status, relevant.Select(test => test.DisplayName).ToArray(), kill, duration, error);
    return mutationResult;
  }
}
