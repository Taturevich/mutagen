using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace MutationFramework.Sdk;

internal static class TestResultClassifier
{
  internal static Dictionary<string, PlatformTestResult> InterruptedResults(IReadOnlyList<TestCaseDescriptor> tests,
      IEnumerable<TestResult> observedResults, bool capturePerTestCoverage, IReadOnlyDictionary<string, string>? expectedSites, string error,
      TestFailureKind interruption = TestFailureKind.Infrastructure)
  {
    var observed = observedResults.Where(result => result.Outcome != TestOutcome.None)
        .GroupBy(result => result.TestCase.Id).ToDictionary(group => group.Key, group => group.First());
    return tests.ToDictionary(test => test.Id, test => observed.TryGetValue(test.Case.Id, out var result)
        ? ConvertResult(test, result, capturePerTestCoverage, expectedSites?.GetValueOrDefault(test.Id))
        : new PlatformTestResult(interruption, error) { State = TestExecutionState.Missing, ExecutionObserved = false }, StringComparer.Ordinal);
  }
  internal static Dictionary<string, PlatformTestResult> PartialResults(IReadOnlyList<TestCaseDescriptor> tests,
      IEnumerable<TestResult> observedResults, bool capturePerTestCoverage, IReadOnlyDictionary<string, string>? expectedSites = null, IReadOnlySet<Guid>? startedTests = null)
  {
    var expectedIds = tests.Select(test => test.Case.Id).ToHashSet();
    var results = observedResults.Where(result => expectedIds.Contains(result.TestCase.Id))
        .GroupBy(result => result.TestCase.Id).ToDictionary(group => group.Key, group => group.First());
    return tests.ToDictionary(test => test.Id, test =>
    {
      if (results.TryGetValue(test.Case.Id, out var result) && result.Outcome != TestOutcome.None)
      {
        return ConvertResult(test, result, capturePerTestCoverage, expectedSites?.GetValueOrDefault(test.Id));
      }
      var started = startedTests?.Contains(test.Case.Id) == true;
      return new PlatformTestResult(TestFailureKind.Bailed,
          started ? "BAILED: Started test canceled after an attributable failure killed this mutant."
              : "BAILED: Not executed after an attributable failure killed this mutant.")
      {
        State = started ? TestExecutionState.Canceled : TestExecutionState.NotExecuted,
        ExecutionObserved = started
      };
    }, StringComparer.Ordinal);
  }

  internal static PlatformTestResult ConvertResult(TestCaseDescriptor test, TestResult result, bool capturePerTestCoverage, string? expectedActiveSite = null)
  {
    var durationMs = Math.Max(0, (long)result.Duration.TotalMilliseconds);
    var converted = result.Outcome switch
    {
      TestOutcome.Passed => new(TestFailureKind.None, DurationMs: durationMs),
      TestOutcome.Skipped => new(TestFailureKind.Skipped, "INCONCLUSIVE: Selected test was skipped.", DurationMs: durationMs),
      TestOutcome.Failed => ClassifyFailure(test, result) with { DurationMs = durationMs },
      _ => new(TestFailureKind.Infrastructure, $"INFRASTRUCTURE: Unexpected test outcome '{result.Outcome}'.", DurationMs: durationMs)
    };
    converted = converted with
    {
      State = result.Outcome == TestOutcome.Skipped ? TestExecutionState.Skipped : TestExecutionState.Completed,
      ExecutionObserved = result.Outcome != TestOutcome.None
    };
    PlatformTestResult MissingCoverage(string error) => converted with
    {
      CoverageError = error,
      FailureKind = expectedActiveSite is not null && converted.FailureKind == TestFailureKind.None ? TestFailureKind.Unattributed : converted.FailureKind,
      Error = converted.Error ?? "INCONCLUSIVE: Mutation execution coverage could not be verified."
    };
    if (!capturePerTestCoverage)
    {
      return converted;
    }

    var properties = result.GetProperties().ToDictionary(pair => pair.Key.Id, pair => pair.Value?.ToString(), StringComparer.Ordinal);
    if (properties.TryGetValue(MutationFramework.CoverageCollector.MutationCoverageCollector.ErrorProperty, out var coverageError))
    {
      return MissingCoverage(coverageError ?? "Coverage collection failed.");
    }

    if (!properties.TryGetValue(MutationFramework.CoverageCollector.MutationCoverageCollector.CoverageProperty, out var coverage))
    {
      return MissingCoverage("VSTest did not return per-test coverage from the in-process collector.");
    }

    var ids = (coverage ?? string.Empty).Trim(';')
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (ids.Any(id => !int.TryParse(id, out _)))
    {
      return MissingCoverage("The in-process collector returned malformed site identifiers.");
    }

    // Baselines must have passed before scheduling. A failed exact selected case is
    // attributable only after its responsible activated site actually executed. VSTest
    // does not expose a portable setup/body/teardown phase; no phase is inferred here.
    if (converted.FailureKind == TestFailureKind.Unattributed && expectedActiveSite is not null &&
        ids.Contains(expectedActiveSite, StringComparer.Ordinal))
    {
      converted = converted with
      {
        FailureKind = TestFailureKind.TestBody
      };
    }
    if (expectedActiveSite is not null && converted.FailureKind == TestFailureKind.None && !ids.Contains(expectedActiveSite, StringComparer.Ordinal))
    {
      converted = converted with
      {
        FailureKind = TestFailureKind.Unattributed,
        Error = "INCONCLUSIVE: Selected test did not execute the activated mutation site."
      };
    }
    return converted with
    {
      CoveredRuntimeSiteIds = ids
    };
  }

  internal static PlatformTestResult ClassifyFailure(TestCaseDescriptor test, TestResult result)
  {
    return new(TestFailureKind.Unattributed, "TEST_FAILURE: " + result.ErrorMessage + Environment.NewLine + result.ErrorStackTrace);
  }
}
