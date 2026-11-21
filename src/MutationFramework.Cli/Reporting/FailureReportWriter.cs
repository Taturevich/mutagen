using System.Text.Json;

namespace MutationFramework.Sdk;

internal static class FailureReportWriter
{
  internal static async Task WriteAsync(string output, string session, Exception exception, long elapsedMs, IReadOnlyList<string> diagnostics)
  {
    try
    {
      var failure = Path.Combine(output, "failure");
      Directory.CreateDirectory(failure);
      await File.WriteAllTextAsync(Path.Combine(failure, "phase-failure.json"), JsonSerializer.Serialize(new
      {
        Error = exception.ToString(),
        ElapsedMs = elapsedMs,
        Diagnostics = diagnostics,
        Canceled = exception is OperationCanceledException
      }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
      var workspace = Path.Combine(session, "workspace");
      if (!Directory.Exists(workspace))
      {
        return;
      }
      foreach (var map in Directory.EnumerateFiles(workspace, "*.mutagen-map.json", SearchOption.AllDirectories))
      {
        var destination = Path.Combine(failure, "sources", Path.GetRelativePath(workspace, map));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(map, destination, true);
        var source = map[..^".mutagen-map.json".Length];
        if (File.Exists(source))
        {
          File.Copy(source, destination[..^".mutagen-map.json".Length] + ".txt", true);
        }
      }
    }
    catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
    {
      Console.Error.WriteLine("Mutagen: could not persist failure diagnostics: " + writeError.Message);
    }
  }
}
