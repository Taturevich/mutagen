using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal static class BuildInputs
{
  internal static List<string> GeneratedCompileInputs(string project, out string? unsafeReason)
  {
    unsafeReason = null;
    var intermediateDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, "obj");
    if (!Directory.Exists(intermediateDirectory))
    {
      return [];
    }

    var inputs = new List<string>();
    foreach (var source in Directory.EnumerateFiles(intermediateDirectory, "*.cs", SearchOption.AllDirectories))
    {
      if (!IsKnownSdkGeneratedCompileSource(source))
      {
        unsafeReason ??= $"unrecognized generated C# compile input '{source}'";
        continue;
      }

      inputs.Add(Path.GetFullPath(source));
    }

    return inputs;
  }

  internal static bool IsKnownSdkGeneratedCompileSource(string path)
  {
    var name = Path.GetFileName(path);
    return name.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);
  }

  internal static string? FindUnsafeCodeGenerationTarget(IEnumerable<string> configurationInputs)
  {
    foreach (var path in configurationInputs.Where(path => new[] { ".csproj", ".props", ".targets" }
                 .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
    {
      if (!File.Exists(path) || IsBuiltInTestSdkTarget(path) || IsBuiltInTestPlatformTarget(path) || IsDotNetSdkFile(path))
      {
        continue;
      }

      XDocument document;
      try { document = XDocument.Load(path); }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
      {
        return path;
      }

      var generatesCompileInputs = document.Descendants().Where(element => element.Name.LocalName == "Target")
          .SelectMany(target => target.Descendants())
          .Any(element => element.Name.LocalName == "Exec" || element.Name.LocalName == "Compile" &&
              (element.Attribute("Include") is not null || element.Attribute("Update") is not null));
      if (generatesCompileInputs)
      {
        return path;
      }
    }

    return null;
  }

  internal static bool IsBuiltInTestSdkTarget(string path) =>
      Path.GetFileName(path).Equals("Microsoft.NET.Test.Sdk.targets", StringComparison.OrdinalIgnoreCase) &&
      path.Split(Path.DirectorySeparatorChar).Contains("microsoft.net.test.sdk", StringComparer.OrdinalIgnoreCase);

  internal static bool IsBuiltInTestPlatformTarget(string path) =>
      Path.GetFileName(path).Equals("Microsoft.Testing.Platform.MSBuild.targets", StringComparison.OrdinalIgnoreCase) &&
      path.Split(Path.DirectorySeparatorChar).Contains("microsoft.testing.platform.msbuild", StringComparer.OrdinalIgnoreCase);

  internal static bool IsDotNetSdkFile(string path)
  {
    var fullPath = Path.GetFullPath(path);
    var dotnetRoots = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.GetFullPath(directory.Trim('"')))
        .Where(directory => File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")));
    return dotnetRoots.Any(root => PathSafety.IsWithinDirectory(fullPath, Path.Combine(root, "sdk")));
  }

  internal static Dictionary<string, string> SnapshotCompilationSources(string root)
  {
    return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("bin", StringComparer.OrdinalIgnoreCase))
        .ToDictionary(path => Path.GetRelativePath(root, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
  }

  internal static string? FindUnsafeGeneratedCompileChange(string root, IReadOnlyDictionary<string, string> before)
  {
    var after = SnapshotCompilationSources(root);
    foreach (var relativePath in before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                 .Order(StringComparer.OrdinalIgnoreCase))
    {
      var hadBefore = before.TryGetValue(relativePath, out var beforeHash);
      var hasAfter = after.TryGetValue(relativePath, out var afterHash);
      if (hadBefore == hasAfter && string.Equals(beforeHash, afterHash, StringComparison.Ordinal))
      {
        continue;
      }

      var fullPath = Path.Combine(root, relativePath);
      var isSdkGenerated = relativePath.Split(Path.DirectorySeparatorChar).Contains("obj", StringComparer.OrdinalIgnoreCase) &&
          IsKnownSdkGeneratedCompileSource(fullPath);
      if (!isSdkGenerated)
      {
        return fullPath;
      }
    }

    return null;
  }

  internal static HashSet<string> ProjectBuildContentInputs(string project)
  {
    var root = Path.GetDirectoryName(Path.GetFullPath(project))!;
    var excluded = new HashSet<string>(["bin", "obj", ".git", ".mutagen-cache", "artifacts", "node_modules", ".vs", "TestResults"], StringComparer.OrdinalIgnoreCase);
    var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.TryPop(out var directory))
    {
      foreach (var file in Directory.EnumerateFiles(directory))
      {
        inputs.Add(Path.GetFullPath(file));
      }

      foreach (var child in Directory.EnumerateDirectories(directory))
      {
        if (!excluded.Contains(Path.GetFileName(child)))
        {
          pending.Push(child);
        }
      }
    }

    return inputs;
  }

  internal static HashSet<string> PackageAssetInputs(string assetsFile)
  {
    using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
    var root = document.RootElement;
    var packageFolders = root.GetProperty("packageFolders").EnumerateObject().Select(property => Path.GetFullPath(property.Name)).ToArray();
    var libraries = root.GetProperty("libraries");
    var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!root.TryGetProperty("targets", out var targets))
    {
      return inputs;
    }

    foreach (var target in targets.EnumerateObject())
    {
      foreach (var library in target.Value.EnumerateObject())
      {
        if (!libraries.TryGetProperty(library.Name, out var libraryMetadata) ||
            !libraryMetadata.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase) ||
            !libraryMetadata.TryGetProperty("path", out var packagePathElement))
        {
          continue;
        }

        var packagePath = packagePathElement.GetString();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
          continue;
        }

        foreach (var assetGroup in library.Value.EnumerateObject())
        {
          foreach (var assetPath in EnumerateAssetPaths(assetGroup.Value))
          {
            if (assetPath.EndsWith("/_._", StringComparison.Ordinal) || assetPath == "_._")
            {
              continue;
            }

            foreach (var packageFolder in packageFolders)
            {
              var fullPath = Path.GetFullPath(Path.Combine(packageFolder, packagePath, assetPath.Replace('/', Path.DirectorySeparatorChar)));
              if (File.Exists(fullPath))
              {
                inputs.Add(fullPath);
              }
            }
          }
        }
      }
    }

    return inputs;

    static IEnumerable<string> EnumerateAssetPaths(JsonElement element)
    {
      if (element.ValueKind == JsonValueKind.Object)
      {
        foreach (var property in element.EnumerateObject())
        {
          if (property.Name.Contains('/') && Path.HasExtension(property.Name))
          {
            yield return property.Name;
          }
          else
          {
            foreach (var nested in EnumerateAssetPaths(property.Value))
            {
              yield return nested;
            }
          }
        }
      }
      else if (element.ValueKind == JsonValueKind.Array)
      {
        foreach (var item in element.EnumerateArray())
        {
          foreach (var nested in EnumerateAssetPaths(item))
          {
            yield return nested;
          }
        }
      }
    }
  }

  internal static bool IsStableTestDiscoveryInput(string path)
  {
    var name = Path.GetFileName(path);
    return !name.StartsWith(".msCoverageSourceRootsMapping", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("nunit_random_seed.tmp", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith(".stryker-unchanged", StringComparison.OrdinalIgnoreCase);
  }

  internal static string[] ProjectConfigurationInputs(string project, out string? unresolvedImport)
  {
    var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project))!;
    var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(project) };
    unresolvedImport = null;
    var pending = new Stack<string>();
    pending.Push(projectDirectory);
    while (pending.TryPop(out var directory))
    {
      foreach (var child in Directory.EnumerateDirectories(directory))
      {
        if (!new[] { "bin", "obj", ".git", ".mutagen-cache", "artifacts", "node_modules" }
            .Any(name => Path.GetFileName(child).Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
          pending.Push(child);
        }
      }
      foreach (var path in Directory.EnumerateFiles(directory))
      {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) || extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
          inputs.Add(Path.GetFullPath(path));
        }
      }
    }
    var projectName = Path.GetFileName(project);
    foreach (var path in new[]
    {
      Path.Combine(projectDirectory, "obj", "project.assets.json"),
      Path.Combine(projectDirectory, "obj", projectName + ".nuget.g.props"),
      Path.Combine(projectDirectory, "obj", projectName + ".nuget.g.targets")
    })
    {
      inputs.Add(path);
    }
    for (var directory = new DirectoryInfo(projectDirectory); directory is not null; directory = directory.Parent)
    {
      foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config" })
      {
        var path = Path.Combine(directory.FullName, name);
        if (File.Exists(path))
        {
          inputs.Add(path);
        }
      }
    }

    var importedFiles = new Stack<string>(inputs.Where(path => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".props", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".targets", StringComparison.OrdinalIgnoreCase)));
    var inspectedImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var inspectedPackageSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var nugetPackageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (string.IsNullOrWhiteSpace(nugetPackageRoot))
    {
      nugetPackageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }
    nugetPackageRoot = Path.GetFullPath(nugetPackageRoot) + Path.DirectorySeparatorChar;
    while (importedFiles.TryPop(out var importingFile))
    {
      if (!inspectedImports.Add(importingFile) || !File.Exists(importingFile))
      {
        continue;
      }

      XDocument document;
      try { document = XDocument.Load(importingFile); }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
      {
        unresolvedImport ??= $"cannot inspect imported file '{importingFile}': {exception.Message}";
        continue;
      }

      var packageRelativePath = Path.GetRelativePath(nugetPackageRoot, importingFile);
      var packageSegments = packageRelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
          StringSplitOptions.RemoveEmptyEntries);
      if (!Path.IsPathRooted(packageRelativePath) && packageSegments.Length >= 2 &&
          !packageSegments[0].Equals("..", StringComparison.Ordinal))
      {
        var packageDirectory = Path.Combine(nugetPackageRoot, packageSegments[0], packageSegments[1]);
        if (Directory.Exists(packageDirectory) && inspectedPackageSources.Add(packageDirectory))
        {
          inputs.UnionWith(Directory.EnumerateFiles(packageDirectory, "*.cs", SearchOption.AllDirectories)
              .Select(Path.GetFullPath));
        }
      }

      foreach (var import in document.Descendants().Where(element => element.Name.LocalName == "Import"))
      {
        var importPath = import.Attribute("Project")?.Value;
        if (string.IsNullOrWhiteSpace(importPath))
        {
          continue;
        }

        importPath = importPath.Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(importingFile) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileFullPath)", importingFile, StringComparison.OrdinalIgnoreCase)
            .Replace("$(NuGetPackageRoot)", nugetPackageRoot, StringComparison.OrdinalIgnoreCase);
        importPath = Regex.Replace(importPath,
            @"\$\(\[System\.IO\.Path\]::GetFileName\(\$\(\[System\.IO\.Path\]::GetDirectoryName\((?<path>[^()]*)\)\)\)\)",
            match => Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(match.Groups["path"].Value)) ?? string.Empty),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (importPath.IndexOfAny(['*', '?']) >= 0)
        {
          unresolvedImport ??= $"'{importingFile}' imports wildcard path '{importPath}', whose resolved file set is not tracked safely";
          continue;
        }
        if (importPath.Contains("$(", StringComparison.Ordinal))
        {
          unresolvedImport ??= $"'{importingFile}' imports unresolved path '{importPath}'";
          continue;
        }
        var resolved = Path.GetFullPath(Path.IsPathRooted(importPath) ? importPath : Path.Combine(Path.GetDirectoryName(importingFile)!, importPath));
        if (File.Exists(resolved) && inputs.Add(resolved))
        {
          importedFiles.Push(resolved);
        }
      }
    }
    return inputs.Order(StringComparer.OrdinalIgnoreCase).ToArray();
  }
}
