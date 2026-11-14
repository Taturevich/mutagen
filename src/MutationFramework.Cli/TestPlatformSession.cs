using System.Xml.Linq;
using System.Diagnostics;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer.Interfaces;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace MutationFramework.Sdk;

internal sealed record TestCaseDescriptor(TestCase Case, string Id, string FullyQualifiedName, string DisplayName, string ExecutorUri, string SourceAssembly);
internal sealed record PlatformTestResult(bool Passed, bool Skipped, string? Error, bool TimedOut, bool TestBodyFailure = false);

internal sealed class TestPlatformSession : IDisposable
{
    private readonly IVsTestConsoleWrapper _console;
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
        if (handler.Aborted || !handler.Completed) throw new InvalidOperationException("VSTest discovery did not complete successfully. " + handler.Error);
        return handler.Cases.Select(test =>
        {
            string source = Path.GetFullPath(test.Source);
            if (stableSourcePaths?.TryGetValue(source, out var stableSource) == true) source = stableSource;
            return new TestCaseDescriptor(test, StableTestId(test, source), test.FullyQualifiedName, test.DisplayName, test.ExecutorUri.ToString(), source);
        }).ToArray();
    }

    public PlatformTestResult Execute(TestCaseDescriptor test, string probePath, string? activeMutationId, int timeoutSeconds, Action hostStarted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handler = new RunHandler();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var registration = timeout.Token.Register(() => { try { _console.CancelTestRun(); } catch { } });
        try
        {
            string settings = RunSettings(probePath, activeMutationId);
            var launcher = new ProcessTestHostLauncher(probePath, activeMutationId, hostStarted);
            _console.RunTestsWithCustomTestHost([test.Case], settings, new TestPlatformOptions(), handler, launcher);
        }
        catch (Exception ex)
        {
            if (timeout.IsCancellationRequested)
                return new(false, false, cancellationToken.IsCancellationRequested ? "CANCELED: Test execution was canceled." : "TIMEOUT: Test execution timed out.", !cancellationToken.IsCancellationRequested);
            return new(false, false, "INFRASTRUCTURE: VSTest execution failed: " + ex.Message, false);
        }

        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        if (timeout.IsCancellationRequested) return new(false, false, "TIMEOUT: Test execution timed out.", true);
        if (!handler.Completed || handler.Aborted || handler.Error != null)
            return new(false, false, "INFRASTRUCTURE: VSTest run did not complete successfully. " + handler.Error, false);
        if (handler.Results.Count != 1 || handler.Results[0].TestCase.Id != test.Case.Id)
            return new(false, false, $"INFRASTRUCTURE: Expected one result for test id {test.Id}, received {handler.Results.Count} results.", false);

        var result = handler.Results[0];
        return result.Outcome switch
        {
            TestOutcome.Passed => new(true, false, null, false),
            TestOutcome.Skipped => new(false, true, "INCONCLUSIVE: Selected test was skipped.", false),
            TestOutcome.Failed => ClassifyFailure(test, result),
            _ => new(false, false, $"INFRASTRUCTURE: Unexpected test outcome '{result.Outcome}'.", false)
        };
    }

    private static PlatformTestResult ClassifyFailure(TestCaseDescriptor test, TestResult result)
    {
        bool testBodyFailure = HasTestBodyFailureEvidence(test.FullyQualifiedName, result.ErrorStackTrace);
        string kind = testBodyFailure ? "TEST_FAILURE" : "TEST_FAILURE_UNATTRIBUTED";
        return new(false, false, kind + ": " + result.ErrorMessage + Environment.NewLine + result.ErrorStackTrace, false, testBodyFailure);
    }

    internal static bool HasTestBodyFailureEvidence(string fullyQualifiedName, string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return false;
        int parameters = fullyQualifiedName.IndexOf('(');
        string methodIdentity = parameters < 0 ? fullyQualifiedName : fullyQualifiedName[..parameters];
        return stackTrace.Contains(methodIdentity, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _console.EndSession();
    }

    private static string StableTestId(TestCase test, string stableSource) => string.Join("::", Path.GetFullPath(stableSource), test.ExecutorUri, test.Id.ToString("N"));

    private static string RunSettings(string probePath, string? activeMutationId)
    {
        var variables = new XElement("EnvironmentVariables",
            new XElement("MUTATION_PROBE_FILE", probePath));
        if (activeMutationId != null) variables.Add(new XElement("MUTATION_ACTIVE_ID", activeMutationId));
        return new XDocument(new XElement("RunSettings", new XElement("RunConfiguration", variables))).ToString(SaveOptions.DisableFormatting);
    }

    private sealed class DiscoveryHandler : ITestDiscoveryEventsHandler2
    {
        public readonly List<TestCase> Cases = [];
        public bool Completed { get; private set; }
        public bool Aborted { get; private set; }
        public string? Error { get; private set; }

        public void HandleDiscoveredTests(IEnumerable<TestCase>? discoveredTestCases) { if (discoveredTestCases != null) Cases.AddRange(discoveredTestCases); }
        public void HandleDiscoveryComplete(DiscoveryCompleteEventArgs discoveryCompleteEventArgs, IEnumerable<TestCase>? lastChunk)
        {
            if (lastChunk != null) Cases.AddRange(lastChunk);
            Completed = true;
            Aborted = discoveryCompleteEventArgs.IsAborted;
        }
        public void HandleRawMessage(string rawMessage) { }
        public void HandleLogMessage(TestMessageLevel level, string? message) { if (level == TestMessageLevel.Error) Error = message; }
    }

    private sealed class RunHandler : ITestRunEventsHandler
    {
        public readonly List<TestResult> Results = [];
        public bool Completed { get; private set; }
        public bool Aborted { get; private set; }
        public string? Error { get; private set; }

        private readonly HashSet<Guid> _resultIds = [];
        private void AddResults(IEnumerable<TestResult>? results)
        {
            if (results == null) return;
            foreach (var result in results) if (_resultIds.Add(result.TestCase.Id)) Results.Add(result);
        }
        public void HandleTestRunComplete(TestRunCompleteEventArgs args, TestRunChangedEventArgs? lastChunkArgs, ICollection<AttachmentSet>? runContextAttachments, ICollection<string>? executorUris)
        {
            if (lastChunkArgs != null) AddResults(lastChunkArgs.NewTestResults);
            Completed = true;
            Aborted = args.IsAborted || args.IsCanceled;
            Error = args.Error?.ToString();
        }
        public void HandleTestRunStatsChange(TestRunChangedEventArgs? args) { if (args != null) AddResults(args.NewTestResults); }
        public int LaunchProcessWithDebuggerAttached(TestProcessStartInfo testProcessStartInfo) => throw new NotSupportedException("Mutation execution does not launch debugger-attached test processes.");
        public void HandleRawMessage(string rawMessage) { }
        public void HandleLogMessage(TestMessageLevel level, string? message) { if (level == TestMessageLevel.Error) Error = message; }
    }

    private sealed class ProcessTestHostLauncher(string probePath, string? activeMutationId, Action hostStarted) : Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces.ITestHostLauncher
    {
        public bool IsDebug => false;
        public int LaunchTestHost(TestProcessStartInfo testProcessStartInfo) => LaunchTestHost(testProcessStartInfo, CancellationToken.None);
        public int LaunchTestHost(TestProcessStartInfo testProcessStartInfo, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(testProcessStartInfo.FileName ?? throw new InvalidOperationException("VSTest did not provide a testhost executable."))
            {
                Arguments = testProcessStartInfo.Arguments,
                WorkingDirectory = testProcessStartInfo.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (testProcessStartInfo.EnvironmentVariables != null)
                foreach (var variable in testProcessStartInfo.EnvironmentVariables) start.Environment[variable.Key] = variable.Value;
            start.Environment["MUTATION_PROBE_FILE"] = probePath;
            if (activeMutationId == null) start.Environment.Remove("MUTATION_ACTIVE_ID");
            else start.Environment["MUTATION_ACTIVE_ID"] = activeMutationId;
            var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch VSTest testhost.");
            hostStarted();
            return process.Id;
        }
    }
}

internal static class TestPlatformPaths
{
    public static string FindConsole(string projectPath)
    {
        string? dotnetRoot = FindDotNetRoot();
        if (string.IsNullOrWhiteSpace(dotnetRoot)) throw new InvalidOperationException("Unable to locate a .NET SDK directory containing vstest.console.dll.");
        string? sdkVersion = null;
        var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
        while (current != null)
        {
            string global = Path.Combine(current.FullName, "global.json");
            if (File.Exists(global))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(global));
                if (doc.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var version)) sdkVersion = version.GetString();
                break;
            }
            current = current.Parent;
        }
        var candidates = Directory.GetDirectories(Path.Combine(dotnetRoot, "sdk"));
        var selected = sdkVersion == null
            ? candidates.OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault()
            : candidates.FirstOrDefault(x => Path.GetFileName(x).Equals(sdkVersion, StringComparison.Ordinal));
        string console = Path.Combine(selected ?? throw new InvalidOperationException("Unable to find the .NET SDK selected for the project."), "vstest.console.dll");
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
            candidates.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(directory => File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))));
        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(Path.Combine(candidate!, "sdk"))) return candidate;
        return null;
    }
}
