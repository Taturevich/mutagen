using System.Diagnostics;
using System.Globalization;

namespace MutationFramework.Sdk;

/// <summary>Owns a proxy and its VSTest child without private translation-layer APIs.</summary>
internal sealed class TestControllerLease : IDisposable
{
  internal const string TargetVariable = "MUTAGEN_VSTEST_PROXY_TARGET";
  internal const string LeaseVariable = "MUTAGEN_VSTEST_PROXY_LEASE";
  private readonly string _path;

  internal TestControllerLease(string? diagnosticDirectory)
  {
    var directory = diagnosticDirectory ?? Path.Combine(Path.GetTempPath(), "mutation-framework", "controller-leases");
    Directory.CreateDirectory(directory);
    _path = Path.GetFullPath(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".controller"));
    File.WriteAllText(_path + ".active", "active");
  }

  internal Dictionary<string, string?> EnvironmentVariables(string consolePath) => new()
  {
    [TargetVariable] = Path.GetFullPath(consolePath),
    [LeaseVariable] = _path
  };

  internal static async Task<int> RunProxyAsync(string target, string[] arguments)
  {
    var lease = Environment.GetEnvironmentVariable(LeaseVariable) ?? throw new InvalidOperationException("Missing controller lease.");
    if (!File.Exists(lease + ".active"))
    {
      return 5;
    }
    using var current = Process.GetCurrentProcess();
    await File.WriteAllTextAsync(lease, current.Id.ToString(CultureInfo.InvariantCulture) + "|" + current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
    if (!File.Exists(lease + ".active"))
    {
      File.Delete(lease);
      return 5;
    }
    var start = new ProcessStartInfo(Environment.ProcessPath ?? "dotnet") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(target);
    foreach (var argument in arguments)
    {
      start.ArgumentList.Add(ParentArgument(argument, current.Id));
    }
    start.Environment.Remove(TargetVariable);
    start.Environment.Remove(LeaseVariable);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start owned VSTest controller.");
    await process.WaitForExitAsync();
    return process.ExitCode;
  }

  internal static string ParentArgument(string argument, int proxyId) =>
      argument.StartsWith("--parentprocessid:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("/parentprocessid:", StringComparison.OrdinalIgnoreCase)
          ? argument[..(argument.IndexOf(':') + 1)] + proxyId.ToString(CultureInfo.InvariantCulture) : argument;

  internal void Terminate()
  {
    try
    {
      // Revoke before reading registration: a late proxy must not launch a child.
      File.Delete(_path + ".active");
      if (!File.Exists(_path))
      {
        return;
      }
      var identity = File.ReadAllText(_path).Split('|');
      if (identity.Length != 2 || !int.TryParse(identity[0], out var id) || !long.TryParse(identity[1], out var started))
      {
        return;
      }
      using var process = Process.GetProcessById(id);
      if (process.StartTime.ToUniversalTime().Ticks == started)
      {
        ProcessRunner.Terminate(process);
      }
    }
    catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
  }

  public void Dispose()
  {
    Terminate();
    try
    {
      File.Delete(_path);
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
  }
}
