namespace MutationFramework.Sdk;

internal sealed record MutationExecutionContext(MutationOptions Options, string SessionDirectory, string TestConsolePath,
    ResourceLockManager ResourceLocks, RunMetrics Metrics, List<string> Diagnostics)
{
  internal IReadOnlyDictionary<string, long> BaselineDurations { get; init; } = new Dictionary<string, long>();
}
