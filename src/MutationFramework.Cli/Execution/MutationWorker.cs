using System.Diagnostics;

namespace MutationFramework.Sdk;

/// <summary>Owns one worker's controller, including replacement after a failed execution.</summary>
internal sealed class MutationWorker : IDisposable
{
  private readonly string _consolePath;
  private readonly RunMetrics _metrics;
  private readonly string? _diagnosticDirectory;
  private readonly int _startupTimeoutSeconds;
  internal TestPlatformSession Session
  {
    get; private set;
  }
  internal long InitializationMs
  {
    get; private set;
  }

  internal MutationWorker(string consolePath, RunMetrics metrics, string? diagnosticDirectory = null, int startupTimeoutSeconds = 60)
  {
    _consolePath = consolePath;
    _metrics = metrics;
    _diagnosticDirectory = diagnosticDirectory;
    _startupTimeoutSeconds = startupTimeoutSeconds;
    Session = StartSession();
  }

  internal void Restart()
  {
    Session.Dispose();
    Session = StartSession();
    Interlocked.Increment(ref _metrics.WorkerRestarts);
  }

  private TestPlatformSession StartSession()
  {
    var timer = Stopwatch.StartNew();
    var session = new TestPlatformSession(_consolePath, _metrics.StartedTestRunner, _diagnosticDirectory, _startupTimeoutSeconds);
    InitializationMs += timer.ElapsedMilliseconds;
    return session;
  }

  public void Dispose() => Session.Dispose();
}
