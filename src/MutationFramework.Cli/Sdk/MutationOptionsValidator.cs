using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal static class MutationOptionsValidator
{
  internal static void ValidateMutatorOptions(MutationOptions options)
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

  internal static (string App, string[] Tests) ValidateForRun(MutationOptions opt)
  {
    MutationOptionsValidator.ValidateMutatorOptions(opt);
    if (opt.MaxWorkers < 1 || opt.TimeoutSeconds < 1)
    {
      throw new ArgumentException("Workers and timeout must be positive.");
    }

    if (!Enum.IsDefined(opt.ExecutionMode))
    {
      throw new ArgumentException("Execution mode must be Auto, Isolated, or Batched.");
    }

    return ValidateProjects(opt, "At least one NUnit/VSTest project is required.");
  }

  internal static (string App, string[] Tests) ValidateForDiscovery(MutationOptions opt)
  {
    ValidateMutatorOptions(opt);
    return ValidateProjects(opt, "At least one test project is required.");
  }

  private static (string App, string[] Tests) ValidateProjects(MutationOptions opt, string requiredTestsMessage)
  {
    var app = Path.GetFullPath(opt.ProjectPath);
    var tests = opt.TestProjects.Select(Path.GetFullPath).ToArray();
    if (tests.Length == 0)
    {
      throw new ArgumentException(requiredTestsMessage);
    }

    if (!File.Exists(app) || opt.ProductionProjects.Any(project => !File.Exists(project)) || tests.Any(t => !File.Exists(t)))
    {
      throw new FileNotFoundException("Project or test project does not exist.");
    }

    return (app, tests);
  }
}
