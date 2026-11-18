using System.Security.Cryptography;
using System.Text;

using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal static class CacheIdentity
{
  internal static ArtifactCache CreateCache(MutationOptions options, string project) =>
      new(options.CacheDirectory ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, ".mutagen-cache"));

  internal static string EnvironmentFingerprint()
      => string.Join("\n", EnvironmentFingerprintParts());

  internal static string[] EnvironmentFingerprintParts()
  {
    // Exclude only the known per-invocation identifier; other environment variables may configure tests.
    var volatileNames = new HashSet<string>(["CODEX_TOOL_CALL_ID", "PATH"], StringComparer.OrdinalIgnoreCase);
    var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .Where(entry => !volatileNames.Contains((string)entry.Key))
        .Select(entry => $"{entry.Key}={Hash(Convert.ToString(entry.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)}")
        .Order(StringComparer.Ordinal).ToList();
    // Tool invocations add random command-wrapper directories to PATH. Remove only those entries;
    // preserve and hash the order of every other path because tests may launch native tools by name.
    var normalizedPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => Path.GetFullPath(path.Trim('"')))
        .Where(path => !Path.GetFileName(path).StartsWith("codex-arg0", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    values.Add($"PATH={Hash(string.Join("|", normalizedPath))}");
    var dotnetRoots = normalizedPath
        .Where(path => File.Exists(Path.Combine(path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    values.Add($"DOTNET_PATH_ORDER={Hash(string.Join("|", dotnetRoots.Select((root, index) => $"{index}:{root}")))}");
    foreach (var root in dotnetRoots)
    {
      var sdkDirectory = Path.Combine(root, "sdk");
      IEnumerable<string?> versions = Directory.Exists(sdkDirectory)
          ? Directory.GetDirectories(sdkDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal)
          : Array.Empty<string?>();
      values.Add($"DOTNET_SDK_ROOT={Hash(root + ":" + string.Join(",", versions))}");
    }

    var runtimeInputs = string.Join(":", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        Environment.Version, System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture,
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture, TimeZoneInfo.Local.Id,
        System.Globalization.CultureInfo.CurrentCulture.Name, System.Globalization.CultureInfo.CurrentUICulture.Name);
    values.Add($"RUNTIME={Hash(runtimeInputs)}");
    return values.Order(StringComparer.Ordinal).ToArray();
  }

  internal static string EngineVersion() => typeof(MutationEngine).Assembly.ManifestModule.ModuleVersionId.ToString("N") + ":" +
      typeof(MutatorRegistry).Assembly.ManifestModule.ModuleVersionId.ToString("N");

  internal static string HashFileContent(string path)
  {
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
  }

  internal static bool IsRuntimeDependencyFile(string path) =>
      Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".so" or ".dylib" or ".json" or ".config" or ".resources";

  internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
  internal static string MutatorFingerprint(MutationOptions options) => options.Registry?.Fingerprint ?? string.Join(";",
      (options.Mutators ?? MutatorRegistry.CreateDefault().Mutators.Select(mutator => mutator.Id)
          .ToHashSet(StringComparer.Ordinal)).Order(StringComparer.Ordinal));
  internal static string HostVersion() => typeof(TestPlatformSession).Assembly.ManifestModule.ModuleVersionId.ToString("N");
}
