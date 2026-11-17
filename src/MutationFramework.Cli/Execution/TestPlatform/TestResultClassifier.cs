using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace MutationFramework.Sdk;

internal static class TestResultClassifier
{
  internal static Dictionary<string, PlatformTestResult> PartialResults(IReadOnlyList<TestCaseDescriptor> tests,
      IEnumerable<TestResult> observedResults, bool capturePerTestCoverage)
  {
    var expectedIds = tests.Select(test => test.Case.Id).ToHashSet();
    var results = observedResults.Where(result => expectedIds.Contains(result.TestCase.Id))
        .GroupBy(result => result.TestCase.Id).ToDictionary(group => group.Key, group => group.First());
    return tests.ToDictionary(test => test.Id,
        test => results.TryGetValue(test.Case.Id, out var result)
            ? result.Outcome == TestOutcome.None
                ? new PlatformTestResult(TestFailureKind.Bailed, "BAILED: Test execution was canceled after an attributable failure killed this mutant.")
                : ConvertResult(test, result, capturePerTestCoverage)
            : new PlatformTestResult(TestFailureKind.Bailed, "BAILED: Not executed after an attributable test failure killed this mutant."),
        StringComparer.Ordinal);
  }

  internal static PlatformTestResult ConvertResult(TestCaseDescriptor test, TestResult result, bool capturePerTestCoverage)
  {
    var durationMs = Math.Max(0, (long)result.Duration.TotalMilliseconds);
    var converted = result.Outcome switch
    {
      TestOutcome.Passed => new(TestFailureKind.None, DurationMs: durationMs),
      TestOutcome.Skipped => new(TestFailureKind.Skipped, "INCONCLUSIVE: Selected test was skipped.", DurationMs: durationMs),
      TestOutcome.Failed => ClassifyFailure(test, result) with { DurationMs = durationMs },
      _ => new(TestFailureKind.Infrastructure, $"INFRASTRUCTURE: Unexpected test outcome '{result.Outcome}'.", DurationMs: durationMs)
    };
    if (!capturePerTestCoverage)
    {
      return converted;
    }

    var properties = result.GetProperties().ToDictionary(pair => pair.Key.Id, pair => pair.Value?.ToString(), StringComparer.Ordinal);
    if (properties.TryGetValue(MutationFramework.CoverageCollector.MutationCoverageCollector.ErrorProperty, out var coverageError))
    {
      return converted with { CoverageError = coverageError };
    }

    if (!properties.TryGetValue(MutationFramework.CoverageCollector.MutationCoverageCollector.CoverageProperty, out var coverage))
    {
      return converted with { CoverageError = "VSTest did not return per-test coverage from the in-process collector." };
    }

    var ids = (coverage ?? string.Empty).Trim(';')
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (ids.Any(id => !int.TryParse(id, out _)))
    {
      return converted with { CoverageError = "The in-process collector returned malformed site identifiers." };
    }

    return converted with { CoveredRuntimeSiteIds = ids };
  }

  internal static PlatformTestResult ClassifyFailure(TestCaseDescriptor test, TestResult result)
  {
    var testBodyFailure = HasTestBodyFailureEvidence(test.FullyQualifiedName, result.ErrorStackTrace);
    var kind = testBodyFailure ? "TEST_FAILURE" : "TEST_FAILURE_UNATTRIBUTED";
    return new(testBodyFailure ? TestFailureKind.TestBody : TestFailureKind.Unattributed, kind + ": " + result.ErrorMessage + Environment.NewLine + result.ErrorStackTrace);
  }

  internal static bool HasTestBodyFailureEvidence(string fullyQualifiedName, string? stackTrace)
  {
    if (string.IsNullOrWhiteSpace(stackTrace))
    {
      return false;
    }

    var parameters = fullyQualifiedName.IndexOf('(');
    var methodIdentity = parameters < 0 ? fullyQualifiedName : fullyQualifiedName[..parameters];
    return stackTrace.Contains(methodIdentity, StringComparison.Ordinal);
  }

}
