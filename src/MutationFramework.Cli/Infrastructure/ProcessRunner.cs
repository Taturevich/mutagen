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

    if (env != null)
    {
      foreach (var kv in env)
      {
        psi.Environment[kv.Key] = kv.Value;
      }
    }

    using var p = new Process { StartInfo = psi, EnableRaisingEvents = true }; p.Start(); metrics.StartedProcess(exe, args); var stdout = p.StandardOutput.ReadToEndAsync(ct); var stderr = p.StandardError.ReadToEndAsync(ct);
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct); timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout)); var timed = false;
    try { await p.WaitForExitAsync(timeoutCts.Token); }
    catch (OperationCanceledException)
    {
      timed = !ct.IsCancellationRequested; Terminate(p);
      if (ct.IsCancellationRequested)
      {
        throw;
      }
    }
    var output = await stdout + await stderr; return new(timed ? -1 : p.ExitCode, output);
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
