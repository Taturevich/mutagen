using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed record BaselineCoverage(TestCaseDescriptor[] TestCases,
    Dictionary<string, TestCaseDescriptor> TestsById, IReadOnlyDictionary<string, ResourceGroup> ResourceGroupsByTestId,
    SortedDictionary<string, IReadOnlyList<string>> TestToSites, Dictionary<string, TestRun> BaselineByName,
    SortedDictionary<string, CoverageStatus> Statuses, long DiscoveryMs, long BaselineMs, long CoverageMs,
    long ProbeSerializationMs, long CoverageMappingMs);
internal sealed class CoverageCollector(MutationOptions options, ArtifactCache cache, List<string> diagnostics, RunMetrics metrics)
{
  internal async Task<BaselineCoverage> CollectAsync(MutationProjects projects, PreparedWorkspace workspace,
      CompiledMutations compilation, List<Candidate> candidates, ResourceLockManager resourceLockManager, CancellationToken ct)
  {
    var opt = options;
    var clonedTests = workspace.TestProjects;
    var tests = projects.Tests;
    var session = workspace.Session;
    var testDlls = compilation.TestDlls;
    var timer = Stopwatch.StartNew();
    var coverageMap = new CoverageMap(candidates);
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
    MutationOptionsValidator.ValidateResourceLockIdentities(opt.ResourceLocksByTestId, discoveredTestCases.Select(testCase => testCase.Id));
    var testCases = discoveredTestCases
        .Where(t => ExecutionPolicy.MatchesTestFilter(t, opt.TestFilter))
        .OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
    var testCasesById = testCases.ToDictionary(testCase => testCase.Id, StringComparer.Ordinal);
    var discoveryMs = timer.ElapsedMilliseconds;
    if (testCases.Length == 0)
    {
      throw new InvalidOperationException("Test discovery failed or returned no tests.");
    }
    if (opt.TestHostIsolationPolicy is TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup)
    {
      var unsupportedTests = testCases.Where(testCase => !ExecutionPolicy.SupportsSerializedRunSettings(testCase.ExecutorUri))
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

    long coverageElapsed = 0, probeSerializationMs = 0, coverageMappingMs = 0;
    timer.Restart();
    var perTestCoverageInSharedHost =
        opt.TestHostIsolationPolicy is TestHostIsolationPolicy.PerMutant or TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup &&
        testCases.All(testCase => ExecutionPolicy.SupportsSerializedRunSettings(testCase.ExecutorUri));
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
                () => TestExecutor.ExecuteTestsWithPerTestCoverage(groupedTests, opt, platform, metrics, ct), ct);
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
            () => TestExecutor.ExecuteTestsWithPerTestCoverage(testCases, opt, baselineSession, metrics, ct), ct);
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
            var run = await TestExecutor.ExecuteTest(testCase, session, opt, null, platform, resourceLockManager, metrics, ct);
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
        CoverageMap.ProbeRead coverage;
        try
        {
          if (run.CoverageError is not null)
          {
            throw new CoverageCollectionException(CoverageStatus.CoverageUnavailable,
                $"CoverageUnavailable: per-test VSTest collector failed for '{testCase.DisplayName}': {run.CoverageError}");
          }
          var stableAssemblyIdentities = testDlls.Select(assembly => stableTestSources[Path.GetFullPath(assembly)]).ToArray();
          coverage = perTestCoverageInSharedHost
              ? await coverageMap.ReadCoverageSitesCached(run.CoveredRuntimeSiteIds, testCase.Id, stableAssemblyIdentities, cache, diagnostics, ct)
              : await coverageMap.ReadProbeSitesCached(run.ProbePath, testCase.Id, stableAssemblyIdentities, cache, diagnostics, ct);
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
    var baselineMs = timer.ElapsedMilliseconds - coverageElapsed;
    if (baselineByName.Values.Any(x => !x.Passed && !x.Skipped))
    {
      throw new BaselineFailureException("Baseline test failures prevent mutation execution.", diagnostics);
    }


    return new(testCases, testCasesById, resourceGroupsByTestId, testToSites, baselineByName, coverageStatuses,
        discoveryMs, baselineMs, coverageElapsed, probeSerializationMs, coverageMappingMs);
  }
}
