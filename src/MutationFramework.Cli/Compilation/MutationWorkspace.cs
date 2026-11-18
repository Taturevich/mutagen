using System.Xml.Linq;

using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal static class MutationWorkspace
{
  internal static Dictionary<string, string> MaterializeLinkedSources(string workspaceCopy, string solutionRoot,
      IReadOnlyList<LinkedCompileItem> linkedCompileItems)
  {
    var sourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in linkedCompileItems)
    {
      if (!PathSafety.IsWithinDirectory(item.ProjectPath, solutionRoot))
      {
        throw new InvalidOperationException($"Linked source '{item.File}' is owned by a project outside the copied solution tree; linked source remapping is unsupported for that layout.");
      }

      var linkedDirectory = Path.Combine(workspaceCopy, ".mutagen-linked");
      Directory.CreateDirectory(linkedDirectory);
      var destination = Path.Combine(linkedDirectory, CacheIdentity.Hash(Path.GetFullPath(item.File).ToUpperInvariant())[..16] + "_" + Path.GetFileName(item.File));
      if (!File.Exists(destination))
      {
        File.Copy(item.File, destination);
      }
      sourceMap[item.File] = destination;
    }

    foreach (var group in linkedCompileItems.GroupBy(item => item.ProjectPath, StringComparer.OrdinalIgnoreCase))
    {
      var ownerProjectCopy = Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, group.Key));
      var document = XDocument.Load(ownerProjectCopy, LoadOptions.PreserveWhitespace);
      var itemGroup = new XElement(document.Root!.Name.Namespace + "ItemGroup");
      foreach (var linkedFile in group.GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase))
      {
        var item = linkedFile.First();
        var identities = linkedFile.Select(candidate => candidate.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (identities.Any(string.IsNullOrWhiteSpace))
        {
          throw new InvalidOperationException($"Linked source '{item.File}' has no evaluated Compile identity in '{group.Key}'.");
        }

        var mappedInclude = Path.GetRelativePath(Path.GetDirectoryName(ownerProjectCopy)!, sourceMap[item.File]);
        foreach (var identity in identities)
        {
          itemGroup.Add(new XElement(document.Root.Name.Namespace + "Compile", new XAttribute("Remove", identity)));
        }
        itemGroup.Add(new XElement(document.Root.Name.Namespace + "Compile", new XAttribute("Include", mappedInclude)));
      }
      document.Root.Add(itemGroup);
      document.Save(ownerProjectCopy, SaveOptions.DisableFormatting);
    }

    return sourceMap;
  }

  internal static void StageExternalWorkspaceConfiguration(string workspaceCopy, string solutionRoot,
      IReadOnlyList<ProjectEvaluation> evaluations, IReadOnlyList<LinkedCompileItem> linkedCompileItems,
      IReadOnlyDictionary<string, string> linkedSourceMap)
  {
    var fullRoot = Path.GetFullPath(solutionRoot);
    var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (string.IsNullOrWhiteSpace(packageRoot))
    {
      packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    packageRoot = Path.GetFullPath(packageRoot);
    var projects = evaluations.SelectMany(evaluation => evaluation.ProjectPaths).Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var supportedExternalImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" })
    {
      var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var project in projects)
      {
        var configuration = FindNearestConfiguration(project, name);
        if (configuration is null || PathSafety.IsWithinDirectory(configuration, fullRoot))
        {
          continue;
        }

        imports[project] = configuration;
        CollectStaticImports(configuration, supportedExternalImports);
      }

      if (imports.Count == 0)
      {
        continue;
      }

      if (name == "global.json")
      {
        var distinctConfigs = imports.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinctConfigs.Length != 1)
        {
          throw new InvalidOperationException("Projects in the copied solution resolve different external global.json files; select one SDK configuration for the mutation workspace.");
        }

        var destination = Path.Combine(workspaceCopy, name);
        if (!File.Exists(destination))
        {
          File.Copy(distinctConfigs[0], destination);
        }
      }
      else
      {
        var wrapperPath = Path.Combine(workspaceCopy, name);
        if (File.Exists(wrapperPath))
        {
          throw new InvalidOperationException($"The copied solution contains '{name}' but one or more projects resolve an external copy. Nested configuration resolution cannot be represented safely.");
        }

        var wrapper = new XDocument(new XElement("Project"));
        foreach (var (project, importPath) in imports.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
          var clonedProject = Path.Combine(workspaceCopy, Path.GetRelativePath(fullRoot, project));
          wrapper.Root!.Add(new XElement("Import",
              new XAttribute("Project", importPath),
              new XAttribute("Condition", $"'$(MSBuildProjectFullPath)' == '{clonedProject}'")));
        }

        if (name == "Directory.Build.targets")
        {
          AddExternalLinkedCompileRemapping(wrapper.Root!, workspaceCopy, fullRoot, linkedCompileItems);
        }

        wrapper.Save(wrapperPath, SaveOptions.DisableFormatting);
      }
    }

    foreach (var project in projects)
    {
      var inputs = BuildInputs.ProjectConfigurationInputs(project, out _);
      var nearestConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" })
      {
        var nearest = FindNearestConfiguration(project, name);
        if (nearest is not null)
        {
          nearestConfigs.Add(nearest);
        }
      }

      var externalConfiguration = inputs.FirstOrDefault(path =>
      {
        if (PathSafety.IsWithinDirectory(path, fullRoot) || PathSafety.IsWithinDirectory(path, packageRoot))
        {
          return false;
        }

        if (supportedExternalImports.Contains(path))
        {
          return false;
        }

        var name = Path.GetFileName(path);
        if (name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("global.json", StringComparison.OrdinalIgnoreCase))
        {
          // Ancestor files shadowed by a nearer configuration are not imported by MSBuild.
          return nearestConfigs.Contains(path);
        }

        return Path.GetExtension(path).Equals(".props", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase);
      });
      if (externalConfiguration is not null)
      {
        throw new InvalidOperationException($"Project '{project}' imports unsupported configuration outside the copied solution tree ('{externalConfiguration}'). External Directory.Build.props, Directory.Build.targets, Directory.Packages.props, and global.json are staged into the isolated workspace; other external configuration must be moved under the solution tree.");
      }
    }
  }

  internal static string? FindNearestConfiguration(string project, string fileName)
  {
    for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(project))!); directory is not null; directory = directory.Parent)
    {
      var path = Path.Combine(directory.FullName, fileName);
      if (File.Exists(path))
      {
        return Path.GetFullPath(path);
      }
    }

    return null;
  }

  internal static void CollectStaticImports(string path, HashSet<string> importedFiles)
  {
    if (!File.Exists(path) || !importedFiles.Add(path))
    {
      return;
    }

    XDocument document;
    try
    {
      document = XDocument.Load(path);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
    {
      return;
    }

    foreach (var import in document.Descendants().Where(element => element.Name.LocalName == "Import"))
    {
      var project = import.Attribute("Project")?.Value;
      if (string.IsNullOrWhiteSpace(project))
      {
        continue;
      }

      project = project.Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
          .Replace("$(MSBuildThisFileFullPath)", path, StringComparison.OrdinalIgnoreCase)
          .Replace("$(NuGetPackageRoot)", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
      if (project.Contains("$(", StringComparison.Ordinal) || project.IndexOfAny(['*', '?']) >= 0)
      {
        continue;
      }

      var resolved = Path.GetFullPath(Path.IsPathRooted(project) ? project : Path.Combine(Path.GetDirectoryName(path)!, project));
      if (File.Exists(resolved))
      {
        CollectStaticImports(resolved, importedFiles);
      }
    }
  }

  internal static void AddExternalLinkedCompileRemapping(XElement wrapper, string workspaceCopy, string solutionRoot,
      IReadOnlyList<LinkedCompileItem> linkedCompileItems)
  {
    foreach (var projectGroup in linkedCompileItems.Where(item => !PathSafety.IsWithinDirectory(item.DefiningProjectPath, solutionRoot))
                 .GroupBy(item => item.ProjectPath, StringComparer.OrdinalIgnoreCase))
    {
      var clonedProject = Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, projectGroup.Key));
      var itemGroup = new XElement("ItemGroup",
          new XAttribute("Condition", $"'$(MSBuildProjectFullPath)' == '{clonedProject}'"));
      foreach (var sourceGroup in projectGroup.GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase))
      {
        var identities = sourceGroup.Select(item => item.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (identities.Any(string.IsNullOrWhiteSpace))
        {
          throw new InvalidOperationException($"Linked source '{sourceGroup.Key}' has no evaluated Compile identity in '{projectGroup.Key}'.");
        }

        foreach (var identity in identities)
        {
          itemGroup.Add(new XElement("Compile", new XAttribute("Remove", identity)));
        }
      }
      wrapper.Add(itemGroup);
    }
  }

  internal static string FindProjectTreeRoot(IReadOnlyList<string> projectPaths)
  {
    if (projectPaths.Count == 0)
    {
      throw new ArgumentException("At least one evaluated project path is required.", nameof(projectPaths));
    }

    var projectDirectories = projectPaths.Select(project => Path.GetDirectoryName(Path.GetFullPath(project))!)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var nugetConfigurationDirectories = projectDirectories.SelectMany(directory => AncestorDirectories(directory)
            .Where(ancestor => File.Exists(Path.Combine(ancestor, "NuGet.config"))))
        .Distinct(StringComparer.OrdinalIgnoreCase);
    var directories = projectDirectories.Concat(nugetConfigurationDirectories).ToArray();
    var common = directories[0];
    while (directories.Any(directory => !PathSafety.IsWithinDirectory(directory, common)))
    {
      var parent = Directory.GetParent(common)?.FullName;
      if (parent is null || string.Equals(parent, common, StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException("Production and test projects do not share a safe project-tree root. Place them under a common solution directory.");
      }

      common = parent;
    }
    return common;

    static IEnumerable<string> AncestorDirectories(string directory)
    {
      for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
      {
        yield return current.FullName;
      }
    }
  }

  internal static (int Files, long Bytes) CopyProject(string from, string to)
  {
    static bool Excluded(string path) => path.Split(Path.DirectorySeparatorChar).Any(part =>
        part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        part.Equals(".mutagen-cache", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("artifacts", StringComparison.OrdinalIgnoreCase) || part.Equals(".codex", StringComparison.OrdinalIgnoreCase) ||
        part.Equals(".aws", StringComparison.OrdinalIgnoreCase));
    Directory.CreateDirectory(to);
    var pending = new Stack<string>();
    pending.Push(from);
    var copiedFiles = 0;
    long copiedBytes = 0;
    while (pending.TryPop(out var sourceDirectory))
    {
      var destinationDirectory = Path.Combine(to, Path.GetRelativePath(from, sourceDirectory));
      Directory.CreateDirectory(destinationDirectory);
      foreach (var file in Directory.GetFiles(sourceDirectory))
      {
        File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), true);
        copiedFiles++;
        copiedBytes += new FileInfo(file).Length;
      }

      foreach (var directory in Directory.GetDirectories(sourceDirectory))
      {
        if (!Excluded(Path.GetRelativePath(from, directory)))
        {
          pending.Push(directory);
        }
      }
    }

    return (copiedFiles, copiedBytes);
  }

  internal static Dictionary<string, CandidateSourceLocation> CreateSourceMappings(MutationProjects projects, PreparedWorkspace workspace)
  {
    var projectDirectory = Path.GetDirectoryName(projects.App)!;
    var mappings = workspace.Sources.ToDictionary(pair => Path.GetFullPath(pair.Value),
        pair => new CandidateSourceLocation(Path.GetRelativePath(projectDirectory, pair.Key).Replace('\\', '/'), null), StringComparer.OrdinalIgnoreCase);
    var shared = projects.Production.SourceOwnership.SelectMany(owner => owner.Files.Select(file => (File: file, Owner: owner)))
        .GroupBy(pair => pair.File, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1);
    foreach (var group in shared)
    {
      foreach (var (_, owner) in group)
      {
        mappings[OwnedSourcePath(workspace.Root, workspace.SolutionRoot, owner.Project, group.Key)] =
            new(Path.GetRelativePath(projectDirectory, group.Key).Replace('\\', '/'), owner.Project);
      }
    }
    return mappings;
  }

  private static string OwnedSourcePath(string workspaceCopy, string solutionRoot, string project, string source) =>
      Path.Combine(Path.GetDirectoryName(Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, project)))!,
          "obj", "MutagenOwned", CacheIdentity.Hash(source) + ".cs");

  internal static void RewriteWorkspaceSources(Dictionary<string, string> mappedFiles, List<Candidate> candidates,
      string app, string workspaceCopy, string solutionRoot, ProjectEvaluation evaluation)
  {
    var shared = evaluation.SourceOwnership.SelectMany(owner => owner.Files.Select(file => (File: file, Owner: owner)))
        .GroupBy(pair => pair.File, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).ToArray();
    var sharedPaths = shared.Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    MutationSchemaBuilder.RewriteSources(mappedFiles.Where(pair => !sharedPaths.Contains(pair.Key))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase), candidates,
        Path.GetDirectoryName(app)!, workspaceCopy, evaluation.ParseOptionsByFile);
    foreach (var source in shared)
    {
      foreach (var (_, owner) in source)
      {
        var clonedProject = Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, owner.Project));
        var target = OwnedSourcePath(workspaceCopy, solutionRoot, owner.Project, source.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source.Key, target, overwrite: true);
        var local = candidates.Where(candidate => string.Equals(candidate.OwnerProject, owner.Project, StringComparison.OrdinalIgnoreCase)).ToList();
        MutationSchemaBuilder.RewriteSources(new()
        {
          [source.Key] = target
        }, local, Path.GetDirectoryName(app)!, workspaceCopy,
            new Dictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpParseOptions> { [source.Key] = owner.ParseOptions });
        var document = XDocument.Load(clonedProject, LoadOptions.PreserveWhitespace);
        var ns = document.Root!.Name.Namespace;
        var targetName = "MutagenOwned" + CacheIdentity.Hash(source.Key)[..16];
        if (!document.Root.Elements(ns + "Target").Any(element => (string?)element.Attribute("Name") == targetName))
        {
          document.Root.Add(new XElement(ns + "Target", new XAttribute("Name", targetName), new XAttribute("BeforeTargets", "CoreCompile"),
              new XElement(ns + "ItemGroup",
                  new XElement(ns + "Compile", new XAttribute("Remove", "@(Compile)"),
                      new XAttribute("Condition", $"'%(Compile.FullPath)' == '{Path.GetFullPath(mappedFiles[source.Key])}' Or '%(Compile.FullPath)' == '{Path.GetFullPath(Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, source.Key)))}'")),
                  new XElement(ns + "Compile", new XAttribute("Include", target)))));
          document.Save(clonedProject, SaveOptions.DisableFormatting);
        }
      }
    }
  }

  internal static PreparedWorkspace Prepare(MutationProjects projects, List<Candidate> candidates,
      string session, List<string> diagnostics)
  {
    var app = projects.App;
    var tests = projects.Tests;
    var evaluation = projects.Production;
    var testEvaluations = projects.TestEvaluations;
    var sourceFiles = evaluation.CompileFiles;
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var graphProjects = evaluation.ProjectPaths.Concat(testEvaluations.SelectMany(testEvaluation => testEvaluation.ProjectPaths))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var solutionRoot = MutationWorkspace.FindProjectTreeRoot(graphProjects);
    var workspaceCopy = Path.Combine(session, "workspace");
    var copiedWorkspace = MutationWorkspace.CopyProject(solutionRoot, workspaceCopy);
    var appCopy = Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, Path.GetDirectoryName(app)!));
    var clonedTests = new List<string>();
    foreach (var test in tests)
    {
      clonedTests.Add(Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, test)));
    }

    var copyMs = timer.ElapsedMilliseconds;
    diagnostics.Add($"WorkspaceCopy: projectRoots={graphProjects.Length}, files={copiedWorkspace.Files}, bytes={copiedWorkspace.Bytes}.");
    timer.Restart();
    var mappedFiles = sourceFiles.ToDictionary(f => f, f => Path.Combine(appCopy, Path.GetRelativePath(Path.GetDirectoryName(app)!, f)), StringComparer.OrdinalIgnoreCase);
    var linkedCompileItems = evaluation.LinkedCompileItems.Concat(testEvaluations.SelectMany(testEvaluation => testEvaluation.LinkedCompileItems))
        .DistinctBy(item => (item.File, item.DefiningProjectPath, item.ProjectPath, item.Identity)).ToArray();
    var linkedSourceMap = MutationWorkspace.MaterializeLinkedSources(workspaceCopy, solutionRoot, linkedCompileItems);
    MutationWorkspace.StageExternalWorkspaceConfiguration(workspaceCopy, solutionRoot, [evaluation, .. testEvaluations], linkedCompileItems, linkedSourceMap);
    foreach (var (original, mapped) in linkedSourceMap)
    {
      mappedFiles[original] = mapped;
    }
    RewriteWorkspaceSources(mappedFiles, candidates, app, workspaceCopy, solutionRoot, evaluation);
    // Schemata intentionally preserve source line layout for diagnostics and mutation IDs.
    // Formatting diagnostics apply to original code, not to generated dispatch text.
    var sharedOwners = evaluation.SourceOwnership.SelectMany(owner => owner.Files.Select(file => (File: file, Owner: owner)))
        .GroupBy(pair => pair.File, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1);
    var formattingPaths = mappedFiles.Values.Concat(sharedOwners.SelectMany(group => group.Select(pair =>
        OwnedSourcePath(workspaceCopy, solutionRoot, pair.Owner.Project, group.Key))));
    foreach (var directory in formattingPaths.Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase))
    {
      var config = Path.Combine(directory.Key, ".editorconfig");
      var sections = string.Join("\n", directory.Select(path => $"[{Path.GetFileName(path)}]\ndotnet_diagnostic.IDE0055.severity = none\n"));
      File.AppendAllText(config, "\n# Generated mutation schemata retain original source line positions.\n" + sections);
    }
    MutationRuntimeSource.WriteRuntimeSources(workspaceCopy, solutionRoot, evaluation.SourceOwnership.Select(owner => owner.Project).ToArray(), candidates);
    var instrumentationMs = timer.ElapsedMilliseconds;
    return new(session, workspaceCopy, solutionRoot, appCopy, clonedTests, mappedFiles, copyMs, instrumentationMs);
  }
}

internal sealed record PreparedWorkspace(string Session, string Root, string SolutionRoot, string AppDirectory,
    List<string> TestProjects, Dictionary<string, string> Sources, long CopyMs, long InstrumentationMs);
