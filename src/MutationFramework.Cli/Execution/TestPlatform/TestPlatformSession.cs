using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace MutationFramework.Sdk;

internal sealed record TestCaseDescriptor(TestCase Case, string Id, string FullyQualifiedName, string DisplayName, string ExecutorUri, string SourceAssembly);
internal enum TestFailureKind { None, Skipped, TestBody, Unattributed, Timeout, Canceled, Infrastructure, Bailed }

internal sealed record PlatformTestResult(TestFailureKind FailureKind, string? Error = null, long DurationMs = 0,
    string[]? CoveredRuntimeSiteIds = null, string? CoverageError = null);

internal sealed class TestPlatformSession : IDisposable
{
  private readonly VsTestConsoleWrapper _console;
  private bool _disposed;

  public TestPlatformSession(string testConsolePath, Action processStarted)
  {
    _console = new VsTestConsoleWrapper(testConsolePath);
    _console.StartSession();
    processStarted();
  }

  public IReadOnlyList<TestCaseDescriptor> Discover(IEnumerable<string> testAssemblies, CancellationToken cancellationToken,
      IReadOnlyDictionary<string, string>? stableSourcePaths = null)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var handler = new DiscoveryHandler();
    _console.DiscoverTests(testAssemblies, "<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount></RunConfiguration></RunSettings>", new TestPlatformOptions(), handler);
    cancellationToken.ThrowIfCancellationRequested();
    if (handler.Aborted || !handler.Completed)
    {
      throw new InvalidOperationException("VSTest discovery did not complete successfully. " + handler.Error);
    }

    if (handler.Cases.Count == 0)
    {
      throw new InvalidOperationException("VSTest discovered no tests. " + handler.Error);
    }

    return handler.Cases.Select(test =>
    {
      var source = Path.GetFullPath(test.Source);
      if (stableSourcePaths?.TryGetValue(source, out var stableSource) == true)
      {
        source = stableSource;
      }

      return new TestCaseDescriptor(test, StableTestId(test, source), test.FullyQualifiedName, test.DisplayName, test.ExecutorUri.ToString(), source);
    }).ToArray();
  }

  public PlatformTestResult Execute(TestCaseDescriptor test, string? probePath, string? activeMutationId, int timeoutSeconds, Action hostStarted, CancellationToken cancellationToken)
  {
    return ExecuteMany([test], probePath, activeMutationId, timeoutSeconds, hostStarted, cancellationToken)[test.Id];
  }

  public IReadOnlyDictionary<string, PlatformTestResult> ExecuteMany(IReadOnlyList<TestCaseDescriptor> tests, string? probePath,
      string? activeMutationId, int timeoutSeconds, Action hostStarted, CancellationToken cancellationToken, bool serializeTests = false,
      bool capturePerTestCoverage = false, bool stopAfterAttributableFailure = false)
  {
    if (tests.Count == 0)
    {
      throw new ArgumentException("At least one test is required.", nameof(tests));
    }

    cancellationToken.ThrowIfCancellationRequested();
    var handler = new RunHandler(tests.Count, stopAfterAttributableFailure ? () =>
    {
      try { _console.CancelTestRun(); } catch { }
    }
    : null);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
    using var launcher = new ProcessTestHostLauncher(probePath, activeMutationId, hostStarted, capturePerTestCoverage);
    using var registration = timeout.Token.Register(() =>
    {
      launcher.ScheduleTermination(TimeSpan.FromSeconds(2));
      try { _console.CancelTestRun(); } catch { }
    });
    try
    {
      var settings = TestRunSettings.RunSettings(probePath, activeMutationId, serializeTests, capturePerTestCoverage);
      _console.RunTestsWithCustomTestHost(tests.Select(test => test.Case).ToArray(), settings, new TestPlatformOptions(), handler, launcher);
    }
    catch (Exception ex)
    {
      if (handler.StoppedAfterAttributableFailure && !cancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested)
      {
        return TestResultClassifier.PartialResults(tests, handler.Results, capturePerTestCoverage);
      }

      if (timeout.IsCancellationRequested)
      {
        var canceled = cancellationToken.IsCancellationRequested;
        return tests.ToDictionary(test => test.Id,
            _ => new PlatformTestResult(canceled ? TestFailureKind.Canceled : TestFailureKind.Timeout, canceled ? "CANCELED: Test execution was canceled." : "TIMEOUT: Test execution timed out."),
            StringComparer.Ordinal);
      }

      return tests.ToDictionary(test => test.Id,
          _ => new PlatformTestResult(TestFailureKind.Infrastructure, "INFRASTRUCTURE: VSTest execution failed: " + ex.Message), StringComparer.Ordinal);
    }
    finally
    {
      if (timeout.IsCancellationRequested)
      {
        launcher.TerminateTestHosts();
      }
    }

    if (cancellationToken.IsCancellationRequested)
    {
      throw new OperationCanceledException(cancellationToken);
    }

    if (timeout.IsCancellationRequested)
    {
      return tests.ToDictionary(test => test.Id,
          _ => new PlatformTestResult(TestFailureKind.Timeout, "TIMEOUT: Test execution timed out."), StringComparer.Ordinal);
    }

    if (handler.StoppedAfterAttributableFailure)
    {
      return TestResultClassifier.PartialResults(tests, handler.Results, capturePerTestCoverage);
    }

    if (!handler.Completed || handler.Aborted || handler.Error != null)
    {
      return tests.ToDictionary(test => test.Id,
          _ => new PlatformTestResult(TestFailureKind.Infrastructure, "INFRASTRUCTURE: VSTest run did not complete successfully. " + handler.Error), StringComparer.Ordinal);
    }

    var expectedIds = tests.Select(test => test.Case.Id).ToHashSet();
    var results = handler.Results.Where(result => expectedIds.Contains(result.TestCase.Id)).ToDictionary(result => result.TestCase.Id);
    if (results.Count != tests.Count)
    {
      var error = $"INFRASTRUCTURE: Expected results for {tests.Count} selected tests, received {results.Count}.";
      return tests.ToDictionary(test => test.Id, _ => new PlatformTestResult(TestFailureKind.Infrastructure, error), StringComparer.Ordinal);
    }

    return tests.ToDictionary(test => test.Id, test => TestResultClassifier.ConvertResult(test, results[test.Case.Id], capturePerTestCoverage), StringComparer.Ordinal);
  }

  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    _console.EndSession();
  }

  private static string StableTestId(TestCase test, string stableSource) => string.Join("::", Path.GetFullPath(stableSource), test.ExecutorUri, test.Id.ToString("N"));

  private sealed class DiscoveryHandler : ITestDiscoveryEventsHandler2
  {
    public readonly List<TestCase> Cases = [];
    public bool Completed { get; private set; }
    public bool Aborted { get; private set; }
    public string? Error { get; private set; }

    public void HandleDiscoveredTests(IEnumerable<TestCase>? discoveredTestCases)
    {
      if (discoveredTestCases != null)
      {
        Cases.AddRange(discoveredTestCases);
      }
    }
    public void HandleDiscoveryComplete(DiscoveryCompleteEventArgs discoveryCompleteEventArgs, IEnumerable<TestCase>? lastChunk)
    {
      if (lastChunk != null)
      {
        Cases.AddRange(lastChunk);
      }

      Completed = true;
      Aborted = discoveryCompleteEventArgs.IsAborted;
    }
    public void HandleRawMessage(string rawMessage) { }
    public void HandleLogMessage(TestMessageLevel level, string? message)
    {
      if (level == TestMessageLevel.Error)
      {
        Error = message;
      }
    }
  }

  private sealed class RunHandler : ITestRunEventsHandler
  {
    public readonly List<TestResult> Results = [];
    public bool Completed { get; private set; }
    public bool Aborted { get; private set; }
    public string? Error { get; private set; }
    public bool StoppedAfterAttributableFailure { get; private set; }

    private readonly HashSet<Guid> _resultIds = [];
    private readonly int _expectedResultCount;
    private readonly Action? _stopAfterFailure;
    private int _stopRequested;

    public RunHandler(int expectedResultCount, Action? stopAfterFailure)
    {
      _expectedResultCount = expectedResultCount;
      _stopAfterFailure = stopAfterFailure;
    }

    private void AddResults(IEnumerable<TestResult>? results)
    {
      if (results == null)
      {
        return;
      }

      foreach (var result in results)
      {
        if (_resultIds.Add(result.TestCase.Id))
        {
          Results.Add(result);
          if (Results.Count < _expectedResultCount &&
              result.Outcome == TestOutcome.Failed &&
              TestResultClassifier.HasTestBodyFailureEvidence(result.TestCase.FullyQualifiedName, result.ErrorStackTrace) &&
              _stopAfterFailure is not null && Interlocked.Exchange(ref _stopRequested, 1) == 0)
          {
            StoppedAfterAttributableFailure = true;
            _stopAfterFailure();
          }
        }
      }
    }
    public void HandleTestRunComplete(TestRunCompleteEventArgs args, TestRunChangedEventArgs? lastChunkArgs, ICollection<AttachmentSet>? runContextAttachments, ICollection<string>? executorUris)
    {
      if (lastChunkArgs != null)
      {
        AddResults(lastChunkArgs.NewTestResults);
      }

      Completed = true;
      Aborted = args.IsAborted || args.IsCanceled;
      Error = args.Error?.ToString();
    }
    public void HandleTestRunStatsChange(TestRunChangedEventArgs? args)
    {
      if (args != null)
      {
        AddResults(args.NewTestResults);
      }
    }
    public int LaunchProcessWithDebuggerAttached(TestProcessStartInfo testProcessStartInfo) => throw new NotSupportedException("Mutation execution does not launch debugger-attached test processes.");
    public void HandleRawMessage(string rawMessage) { }
    public void HandleLogMessage(TestMessageLevel level, string? message)
    {
      if (level == TestMessageLevel.Error)
      {
        Error = message;
      }
    }
  }


}
