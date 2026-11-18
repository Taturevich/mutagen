using System.Diagnostics;

namespace MutationFramework.Sdk;

internal static class ProcessRunner
{
  internal static async Task<Proc> RunProcess(string exe, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string?>? env, int timeout, RunMetrics metrics, CancellationToken ct)
  {
    var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in args)
    {
      psi.ArgumentList.Add(arg);
    }

    if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        args.Count > 0 && args[0] is "build" or "restore" or "msbuild")
    {
      // Reused build workers can outlive their parent and keep redirected output open.
      psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
      psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
      if (args[0] == "build" && !args.Contains("--disable-build-servers", StringComparer.Ordinal))
      {
        psi.ArgumentList.Add("--disable-build-servers");
      }
    }

    if (env != null)
    {
      foreach (var kv in env)
      {
        psi.Environment[kv.Key] = kv.Value;
      }
    }

    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
    using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
    p.Start();
    metrics.StartedProcess(exe, args);
    var output = Task.WhenAll(p.StandardOutput.ReadToEndAsync(timeoutCts.Token), p.StandardError.ReadToEndAsync(timeoutCts.Token));
    // A child can exit while a descendant still owns a redirected pipe. The deadline
    // covers draining both streams as well as waiting for the direct process.
    _ = output.ContinueWith(task => _ = task.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    try
    {
      await p.WaitForExitAsync(timeoutCts.Token);
      var streams = await output.WaitAsync(timeoutCts.Token);
      return new(p.ExitCode, string.Concat(streams));
    }
    catch (OperationCanceledException)
    {
      Terminate(p);
      ct.ThrowIfCancellationRequested();
      return new(-1, $"Process timed out after {timeout} seconds while waiting for completion or redirected output.");
    }
  }

  internal sealed record Proc(int ExitCode, string Output);
  internal static void Terminate(Process process)
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
}
