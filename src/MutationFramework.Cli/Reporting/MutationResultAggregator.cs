namespace MutationFramework.Sdk;

internal sealed record RunStatistics(PhaseTimings Timings, RunMetrics Metrics, long DurationMs);

internal static class MutationResultAggregator
{
  internal static MutationRunResult Create(MutationOptions opt, BaselineCoverage baseline,
      PlannedMutations plan, ExecutedMutations execution, RunStatistics statistics,
      IReadOnlyList<MutationResult> unsupportedMutations, IReadOnlyList<string> diagnostics)
  {
    var completed = execution.Results;
    var runCanceled = execution.Canceled;
    var workerBusy = execution.WorkerBusyMs;
    var executionMs = execution.ExecutionMs;
    var executeMutants = execution.Executed;
    var testCases = baseline.TestCases;
    var testToSites = baseline.TestToSites;
    var baselineByName = baseline.BaselineByName;
    var resourceGroupsByTestId = baseline.ResourceGroupsByTestId;
    var coverageStatuses = baseline.Statuses;
    var workItems = plan.WorkItems;
    var executionPlan = plan.ExecutionPlan;
    var perTestIsolationIds = plan.PerTestIsolationIds;
    var perMutantIsolationIds = plan.PerMutantIsolationIds;
    var timings = statistics.Timings;
    var metrics = statistics.Metrics;
    var output = Path.GetFullPath(opt.OutputDirectory);
    var ordered = completed.OrderBy(x => x.SourceFile, StringComparer.Ordinal).ThenBy(x => x.Line).ThenBy(x => x.Column).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    var killed = ordered.Count(x => x.Status == MutationStatus.Killed); var survived = ordered.Count(x => x.Status == MutationStatus.Survived); var denominator = killed + survived;
    var uncovered = ordered.Count(x => x.Status == MutationStatus.NoCoverage);
    var busyMs = workerBusy.Sum();
    var coveredCount = workItems.Count;
    var counters = new ExecutionCounters(ordered.Length, coveredCount, uncovered, testCases.Length, metrics.BaselineTests, metrics.MutationTests, metrics.TestRunnerStarts, metrics.DiscoveryOperations, metrics.BuildInvocations, metrics.WorkerRestarts,
        metrics.WorkerFailures, executionPlan.Batches.Count, executionPlan.Statistics.AverageMutantsPerBatch, busyMs,
        Math.Max(0, executionMs * workerBusy.Length - busyMs))
    { TestHostProcessStarts = metrics.TestHostStarts };
    var overallStatus = !executeMutants ? "Planned" : runCanceled ? "Canceled" : ordered.Any(x => x.Status is MutationStatus.InfrastructureError or MutationStatus.Timeout or MutationStatus.Inconclusive or MutationStatus.CompileError) ? "CompletedWithErrors" : "Completed";
    var result = new MutationRunResult(overallStatus, ordered, killed, survived, uncovered, denominator == 0 ? null : (double)killed * 100 / denominator, timings, statistics.DurationMs, testToSites, diagnostics, Path.Combine(output, "mutation-report.json"), Path.Combine(output, "index.html"))
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
            : ExecutionPolicy.ResolveTestExecutionPolicies(usesPerTestIsolation, usesPerMutantIsolation);
        return new TestExecutionProfile(testCase.Id,
            baselineByName[testCase.Id].DurationMs, testToSites.GetValueOrDefault(testCase.Id, []),
            ExecutionPolicy.GetFixtureAffinity(testCase.FullyQualifiedName), usesPerTestIsolation,
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
    return result;
  }
}
