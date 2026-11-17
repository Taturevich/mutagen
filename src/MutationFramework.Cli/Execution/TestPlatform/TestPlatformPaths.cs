namespace MutationFramework.Sdk;

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
    var selected = SelectSdk(candidates, sdkVersion);
    var console = Path.Combine(selected ?? throw new InvalidOperationException("Unable to find the .NET SDK selected for the project."), "vstest.console.dll");
    return File.Exists(console) ? console : throw new FileNotFoundException("The selected .NET SDK does not contain vstest.console.dll.", console);
  }

  internal static string? SelectSdk(IEnumerable<string> candidates, string? requestedVersion) => requestedVersion is not null
      ? candidates.FirstOrDefault(path => Path.GetFileName(path).Equals(requestedVersion, StringComparison.Ordinal))
      : candidates.Select(path => (Path: path, Name: Path.GetFileName(path)))
          .Select(candidate => (candidate.Path, candidate.Name,
              Version: Version.TryParse(candidate.Name.Split('-')[0], out var version) ? version : null))
          .Where(candidate => candidate.Version is not null)
          .OrderByDescending(candidate => candidate.Version)
          .ThenBy(candidate => candidate.Name.Contains('-'))
          .ThenByDescending(candidate => candidate.Name, StringComparer.Ordinal)
          .Select(candidate => candidate.Path).FirstOrDefault();

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
