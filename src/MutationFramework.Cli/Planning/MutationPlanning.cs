using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed record PlannedMutations(MutationExecutionPlan ExecutionPlan, List<MutationWorkItem> WorkItems,
    HashSet<string> PerTestIsolationIds, HashSet<string> PerMutantIsolationIds, bool CoverageComplete);
internal static class MutationPlanning
{
  internal static PlannedMutations Create(List<Candidate> candidates, CompiledMutations compilation,
      BaselineCoverage baseline, MutationOptions opt, List<string> diagnostics)
  {
    var compileRejected = compilation.Rejected;
    var testCases = baseline.TestCases;
    var testCasesById = baseline.TestsById;
    var testToSites = baseline.TestToSites;
    var baselineByName = baseline.BaselineByName;
    var coverageStatuses = baseline.Statuses;
    var testsById = testCasesById;
    var siteTests = new Dictionary<string, List<TestCaseDescriptor>>(StringComparer.Ordinal);
    foreach (var pair in testToSites)
    {
      foreach (var site in pair.Value)
      {
        if (!siteTests.TryGetValue(site, out var list))
        {
          siteTests[site] = list = [];
        }
        list.Add(testsById[pair.Key]);
      }
    }

    var preclassified = new List<MutationResult>(compileRejected);
    var workItems = new List<MutationWorkItem>();
    foreach (var candidate in candidates)
    {
      var relevant = siteTests.GetValueOrDefault(candidate.SiteId) ?? [];
      if (relevant.Count == 0)
      {
        preclassified.Add(MutationResults.ToResult(candidate, MutationStatus.NoCoverage, [], [], 0));
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
                !ExecutionPolicy.SupportsSerializedRunSettings(testsById[testId].ExecutorUri)))
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

    return new(executionPlan, workItems, perTestIsolationIds, perMutantIsolationIds, coverageComplete);
  }
}
