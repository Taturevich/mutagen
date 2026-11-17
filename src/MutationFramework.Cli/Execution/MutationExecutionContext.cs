namespace MutationFramework.Sdk;

internal sealed record MutationExecutionContext(MutationOptions Options, string SessionDirectory, string TestConsolePath,
    ResourceLockManager ResourceLocks, RunMetrics Metrics, List<string> Diagnostics);
