using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace MutationFramework.Sdk;

internal sealed class ProcessTestHostLauncher(string? probePath, string? activeMutationId, Action hostStarted,
    bool capturePerTestCoverage = false, string? diagnosticDirectory = null, IReadOnlyList<string>? testSources = null) : Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces.ITestHostLauncher, IDisposable
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
      Arguments = ResolveTestHostArguments(testProcessStartInfo.Arguments ?? string.Empty, testSources ?? []),
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

    if (diagnosticDirectory is not null)
    {
      Directory.CreateDirectory(diagnosticDirectory);
      File.WriteAllText(Path.Combine(diagnosticDirectory, Guid.NewGuid().ToString("N") + ".launch.json"), JsonSerializer.Serialize(new
      {
        Executable = start.FileName,
        start.Arguments,
        start.WorkingDirectory,
        Sources = testSources,
        PackageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
      }, new JsonSerializerOptions { WriteIndented = true }));
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
      ProcessRunner.Terminate(process);
    }
    return process.Id;
  }

  internal static string ResolveTestHostArguments(string arguments, IReadOnlyList<string> testSources, string? packageRoot = null)
  {
    // Some VSTest versions return the package-relative runtime asset rather than
    // an executable path for a cloned, multi-source run. Resolve only that asset.
    return Regex.Replace(arguments, "(?<!\\S)\"?microsoft\\.testplatform\\.testhost[/\\\\][^\"\\s]*testhost\\.dll\"?", match =>
    {
      var relative = match.Value.Trim('"').Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
      var roots = new[] { packageRoot, Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages") };
      var resolved = roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => Path.Combine(root!, relative))
          .Concat(testSources.Select(source => Path.Combine(Path.GetDirectoryName(source)!, "testhost.dll")))
          .FirstOrDefault(File.Exists);
      return resolved is null ? match.Value : "\"" + Path.GetFullPath(resolved) + "\"";
    }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
      ProcessRunner.Terminate(process);
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
