using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal static class ExecutionPolicy
{
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

  internal static string? GetFixtureAffinity(string fullyQualifiedName)
  {
    var separator = fullyQualifiedName.LastIndexOf('.');
    return separator > 0 ? fullyQualifiedName[..separator] : null;
  }

  internal static bool MatchesTestFilter(TestCaseDescriptor test, string? filter) =>
      string.IsNullOrWhiteSpace(filter) || filter.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
          .Any(term => test.FullyQualifiedName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
              test.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));
}
