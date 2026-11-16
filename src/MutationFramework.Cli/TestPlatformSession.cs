using System.Xml.Linq;
using System.Diagnostics;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace MutationFramework.Sdk;

internal sealed record TestCaseDescriptor(TestCase Case, string Id, string FullyQualifiedName, string DisplayName, string ExecutorUri, string SourceAssembly);
internal sealed record PlatformTestResult(bool Passed, bool Skipped, string? Error, bool TimedOut, bool TestBodyFailure = false, long DurationMs = 0,
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
      var settings = RunSettings(probePath, activeMutationId, serializeTests, capturePerTestCoverage);
      _console.RunTestsWithCustomTestHost(tests.Select(test => test.Case).ToArray(), settings, new TestPlatformOptions(), handler, launcher);
    }
    catch (Exception ex)
    {
      if (handler.StoppedAfterAttributableFailure && !cancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested)
      {
        return PartialResults(tests, handler.Results, capturePerTestCoverage);
      }

      if (timeout.IsCancellationRequested)
      {
        var canceled = cancellationToken.IsCancellationRequested;
        return tests.ToDictionary(test => test.Id,
            _ => new PlatformTestResult(false, false, canceled ? "CANCELED: Test execution was canceled." : "TIMEOUT: Test execution timed out.", !canceled),
            StringComparer.Ordinal);
      }

      return tests.ToDictionary(test => test.Id,
          _ => new PlatformTestResult(false, false, "INFRASTRUCTURE: VSTest execution failed: " + ex.Message, false), StringComparer.Ordinal);
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
          _ => new PlatformTestResult(false, false, "TIMEOUT: Test execution timed out.", true), StringComparer.Ordinal);
    }

    if (handler.StoppedAfterAttributableFailure)
    {
      return PartialResults(tests, handler.Results, capturePerTestCoverage);
    }

    if (!handler.Completed || handler.Aborted || handler.Error != null)
    {
      return tests.ToDictionary(test => test.Id,
          _ => new PlatformTestResult(false, false, "INFRASTRUCTURE: VSTest run did not complete successfully. " + handler.Error, false), StringComparer.Ordinal);
    }

    var expectedIds = tests.Select(test => test.Case.Id).ToHashSet();
    var results = handler.Results.Where(result => expectedIds.Contains(result.TestCase.Id)).ToDictionary(result => result.TestCase.Id);
    if (results.Count != tests.Count)
    {
      var error = $"INFRASTRUCTURE: Expected results for {tests.Count} selected tests, received {results.Count}.";
      return tests.ToDictionary(test => test.Id, _ => new PlatformTestResult(false, false, error, false), StringComparer.Ordinal);
    }

    return tests.ToDictionary(test => test.Id, test => ConvertResult(test, results[test.Case.Id], capturePerTestCoverage), StringComparer.Ordinal);
  }

  private static Dictionary<string, PlatformTestResult> PartialResults(IReadOnlyList<TestCaseDescriptor> tests,
      IEnumerable<TestResult> observedResults, bool capturePerTestCoverage)
  {
    var expectedIds = tests.Select(test => test.Case.Id).ToHashSet();
    var results = observedResults.Where(result => expectedIds.Contains(result.TestCase.Id))
        .GroupBy(result => result.TestCase.Id).ToDictionary(group => group.Key, group => group.First());
    return tests.ToDictionary(test => test.Id,
        test => results.TryGetValue(test.Case.Id, out var result)
            ? result.Outcome == TestOutcome.None
                ? new PlatformTestResult(false, true, "BAILED: Test execution was canceled after an attributable failure killed this mutant.", false)
                : ConvertResult(test, result, capturePerTestCoverage)
            : new PlatformTestResult(false, true, "BAILED: Not executed after an attributable test failure killed this mutant.", false),
        StringComparer.Ordinal);
  }

  private static PlatformTestResult ConvertResult(TestCaseDescriptor test, TestResult result, bool capturePerTestCoverage)
  {
    var durationMs = Math.Max(0, (long)result.Duration.TotalMilliseconds);
    var converted = result.Outcome switch
    {
      TestOutcome.Passed => new(true, false, null, false, DurationMs: durationMs),
      TestOutcome.Skipped => new(false, true, "INCONCLUSIVE: Selected test was skipped.", false, DurationMs: durationMs),
      TestOutcome.Failed => ClassifyFailure(test, result) with { DurationMs = durationMs },
      _ => new(false, false, $"INFRASTRUCTURE: Unexpected test outcome '{result.Outcome}'.", false, DurationMs: durationMs)
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

  private static PlatformTestResult ClassifyFailure(TestCaseDescriptor test, TestResult result)
  {
    var testBodyFailure = HasTestBodyFailureEvidence(test.FullyQualifiedName, result.ErrorStackTrace);
    var kind = testBodyFailure ? "TEST_FAILURE" : "TEST_FAILURE_UNATTRIBUTED";
    return new(false, false, kind + ": " + result.ErrorMessage + Environment.NewLine + result.ErrorStackTrace, false, testBodyFailure);
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

  internal static string RunSettings(string? probePath, string? activeMutationId, bool serializeTests = false,
      bool capturePerTestCoverage = false)
  {
    var variables = new XElement("EnvironmentVariables");
    if (probePath is not null)
    {
      variables.Add(new XElement("MUTATION_PROBE_FILE", probePath));
    }

    if (activeMutationId != null)
    {
      variables.Add(new XElement("MUTATION_ACTIVE_ID", activeMutationId));
    }

    var runConfiguration = new XElement("RunConfiguration", variables);
    var sections = new List<object> { runConfiguration };
    if (serializeTests)
    {
      runConfiguration.AddFirst(new XElement("MaxCpuCount", 1));
      runConfiguration.Add(new XElement("DisableParallelization", true));
      runConfiguration.Add(new XElement("CollectDataForEachTestSeparately", true));
      sections.Add(new XElement("NUnit", new XElement("NumberOfTestWorkers", 0)));
    }

    if (capturePerTestCoverage)
    {
      var collectorType = typeof(MutationFramework.CoverageCollector.MutationCoverageCollector);
      sections.Add(new XElement("InProcDataCollectionRunSettings",
          new XElement("InProcDataCollectors",
              new XElement("InProcDataCollector",
                  new XAttribute("friendlyName", MutationFramework.CoverageCollector.MutationCoverageCollector.FriendlyName),
                  new XAttribute("uri", MutationFramework.CoverageCollector.MutationCoverageCollector.TypeUri),
                  new XAttribute("codebase", collectorType.Assembly.Location),
                  new XAttribute("assemblyQualifiedName", collectorType.AssemblyQualifiedName!),
                  new XElement("Configuration")))));
    }

    return new XDocument(new XElement("RunSettings", sections)).ToString(SaveOptions.DisableFormatting);
  }

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
              TestPlatformSession.HasTestBodyFailureEvidence(result.TestCase.FullyQualifiedName, result.ErrorStackTrace) &&
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

  private sealed class ProcessTestHostLauncher(string? probePath, string? activeMutationId, Action hostStarted,
      bool capturePerTestCoverage = false) : Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces.ITestHostLauncher, IDisposable
  {
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> _processes = new();
    private readonly CancellationTokenSource _terminationDelay = new();
    private int _terminationScheduled;
    private int _terminationRequested;

    public bool IsDebug => false;
    public int LaunchTestHost(TestProcessStartInfo testProcessStartInfo) => LaunchTestHost(testProcessStartInfo, CancellationToken.None);
    public int LaunchTestHost(TestProcessStartInfo testProcessStartInfo, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (Volatile.Read(ref _terminationRequested) != 0)
      {
        throw new OperationCanceledException("The mutation test timeout has terminated its testhost.");
      }

      var start = new ProcessStartInfo(testProcessStartInfo.FileName ?? throw new InvalidOperationException("VSTest did not provide a testhost executable."))
      {
        Arguments = testProcessStartInfo.Arguments,
        WorkingDirectory = testProcessStartInfo.WorkingDirectory,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      if (testProcessStartInfo.EnvironmentVariables != null)
      {
        foreach (var variable in testProcessStartInfo.EnvironmentVariables)
        {
          start.Environment[variable.Key] = variable.Value;
        }
      }

      if (probePath is null)
      {
        start.Environment.Remove("MUTATION_PROBE_FILE");
      }
      else
      {
        start.Environment["MUTATION_PROBE_FILE"] = probePath;
      }
      if (activeMutationId == null)
      {
        start.Environment.Remove("MUTATION_ACTIVE_ID");
      }
      else
      {
        start.Environment["MUTATION_ACTIVE_ID"] = activeMutationId;
      }
      if (capturePerTestCoverage)
      {
        start.Environment["MUTATION_PER_TEST_COVERAGE"] = "1";
      }
      else
      {
        start.Environment.Remove("MUTATION_PER_TEST_COVERAGE");
      }

      var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch VSTest testhost.");
      if (!_processes.TryAdd(process.Id, process))
      {
        process.Dispose();
        throw new InvalidOperationException($"Testhost process ID {process.Id} was registered more than once.");
      }
      hostStarted();
      if (Volatile.Read(ref _terminationRequested) != 0)
      {
        Terminate(process);
      }
      return process.Id;
    }

    public void ScheduleTermination(TimeSpan gracePeriod)
    {
      if (Interlocked.Exchange(ref _terminationScheduled, 1) != 0)
      {
        return;
      }

      var cancellationToken = _terminationDelay.Token;
      _ = Task.Run(async () =>
      {
        try
        {
          await Task.Delay(gracePeriod, cancellationToken).ConfigureAwait(false);
          TerminateTestHosts();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
      });
    }

    public void TerminateTestHosts()
    {
      Interlocked.Exchange(ref _terminationRequested, 1);
      foreach (var process in _processes.Values)
      {
        Terminate(process);
      }
    }

    private static void Terminate(Process process)
    {
      try
      {
        if (!process.HasExited)
        {
          process.Kill(entireProcessTree: true);
        }
      }
      catch (InvalidOperationException)
      {
      }
      catch (System.ComponentModel.Win32Exception)
      {
      }
      catch (NotSupportedException)
      {
      }
    }

    public void Dispose()
    {
      _terminationDelay.Cancel();
      foreach (var process in _processes.Values)
      {
        process.Dispose();
      }
      _processes.Clear();
      _terminationDelay.Dispose();
    }
  }
}

internal static class TestPlatformPaths
{
  public static string FindConsole(string projectPath)
  {
    var dotnetRoot = FindDotNetRoot();
    if (string.IsNullOrWhiteSpace(dotnetRoot))
    {
      throw new InvalidOperationException("Unable to locate a .NET SDK directory containing vstest.console.dll.");
    }

    string? sdkVersion = null;
    var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
    while (current != null)
    {
      var global = Path.Combine(current.FullName, "global.json");
      if (File.Exists(global))
      {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(global));
        if (doc.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var version))
        {
          sdkVersion = version.GetString();
        }

        break;
      }
      current = current.Parent;
    }
    var candidates = Directory.GetDirectories(Path.Combine(dotnetRoot, "sdk"));
    var selected = sdkVersion == null
        ? candidates.OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault()
        : candidates.FirstOrDefault(x => Path.GetFileName(x).Equals(sdkVersion, StringComparison.Ordinal));
    var console = Path.Combine(selected ?? throw new InvalidOperationException("Unable to find the .NET SDK selected for the project."), "vstest.console.dll");
    return File.Exists(console) ? console : throw new FileNotFoundException("The selected .NET SDK does not contain vstest.console.dll.", console);
  }

  private static string? FindDotNetRoot()
  {
    var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Environment.ProcessPath is { } processPath ? Path.GetDirectoryName(processPath) : null
        };
    var pathVariable = Environment.GetEnvironmentVariable("PATH");
    if (!string.IsNullOrEmpty(pathVariable))
    {
      candidates.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Where(directory => File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))));
    }

    foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
    {
      if (Directory.Exists(Path.Combine(candidate!, "sdk")))
      {
        return candidate;
      }
    }

    return null;
  }
}
