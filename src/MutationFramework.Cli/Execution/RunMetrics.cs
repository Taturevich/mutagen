namespace MutationFramework.Sdk;

internal sealed class RunMetrics
{
  public long SemanticAnalysisMs;
  public int ProcessStarts, TestRunnerStarts, TestHostStarts, BuildInvocations, DiscoveryOperations, BaselineTests, MutationTests, WorkerRestarts, WorkerFailures;
  public void StartedProcess(string exe, IReadOnlyList<string> args)
  {
    Interlocked.Increment(ref ProcessStarts);
    if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
      if (args.Contains("test", StringComparer.OrdinalIgnoreCase))
      {
        Interlocked.Increment(ref TestRunnerStarts);
      }

      if (args.Contains("build", StringComparer.OrdinalIgnoreCase) || args.Contains("-t:Rebuild", StringComparer.OrdinalIgnoreCase))
      {
        Interlocked.Increment(ref BuildInvocations);
      }

      if (args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
      {
        Interlocked.Increment(ref DiscoveryOperations);
      }
    }
  }
  public void StartedTestRunner()
  {
    // One owned proxy and one VSTest controller are started for each session.
    Interlocked.Add(ref ProcessStarts, 2);
    Interlocked.Increment(ref TestRunnerStarts);
  }
  public void StartedTestHost(int count = 1)
  {
    Interlocked.Add(ref ProcessStarts, count);
    Interlocked.Add(ref TestHostStarts, count);
  }
}
