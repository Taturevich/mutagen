using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MutationFramework.Sdk;

internal sealed record CachedTargetFrameworkProperties(string? TargetFramework, string? TargetFrameworks);


internal sealed record CachedProjectEvaluation(string[] CompileFiles, string LanguageVersion, string[] DefineConstants, string? TargetFramework,
    string AssemblyName, string[] ProjectReferences, LinkedCompileItem[] LinkedCompileItems)
{
  public string? TargetPath { get; init; }
  public string[] BuildInputs { get; init; } = [];
  public bool HasUnsafeGeneratedCompileInputs { get; init; }
}


internal static class CompilationWorkspace
{
  internal static async Task<ProjectEvaluation> GetCompileFiles(string project, RunMetrics metrics, CancellationToken ct, string? targetFramework = null,
      ArtifactCache? cache = null, List<string>? diagnostics = null)
  {
    var cacheInputs = BuildInputs.ProjectConfigurationInputs(project, out var unsafeImport);
    var canCache = unsafeImport is null;
    var environmentFingerprint = CacheIdentity.EnvironmentFingerprint();
    var cacheKey = ArtifactCache.ContentKey(cacheInputs,
        ["project-evaluation-v4", targetFramework ?? string.Empty, environmentFingerprint, CacheIdentity.EngineVersion()]);
    var logicalIdentity = Path.GetFullPath(project) + "|" + (targetFramework ?? "default");
    if (cache is not null)
    {
      if (!canCache)
      {
        diagnostics?.Add($"CacheBypass: project evaluation contains an MSBuild import with untracked external dependencies ({unsafeImport}).");
      }
      else
      {
        var cacheRead = await cache.TryReadAsync<CachedProjectEvaluation>("project-evaluation", logicalIdentity, cacheKey, ct);
        diagnostics?.Add(cacheRead.Diagnostic);
        if (cacheRead.Found)
        {
          return CreateProjectEvaluation(cacheRead.Value!, project);
        }
      }

    }
    var args = new List<string> { "msbuild", project,
      "-getItem:Compile,ProjectReference,None,Content,EmbeddedResource,AdditionalFiles,Analyzer,Page,Resource",
      "-getProperty:AssemblyName,LangVersion,DefineConstants,TargetFramework,TargetFrameworks,TargetPath", "-nologo" };
    if (!string.IsNullOrWhiteSpace(targetFramework))
    {
      args.Add($"-p:TargetFramework={targetFramework}");
    }

    var p = await ProcessRunner.RunProcess("dotnet", args, Path.GetDirectoryName(Path.GetFullPath(project))!, null, 60, metrics, ct);
    if (p.ExitCode != 0)
    {
      throw new InvalidOperationException("Unable to evaluate project Compile items: " + p.Output);
    }

    var result = ParseProjectEvaluation(p.Output, project, targetFramework);
    if (cache is not null && canCache)
    {
      await cache.WriteAsync("project-evaluation", logicalIdentity, cacheKey,
          new CachedProjectEvaluation(result.CompileFiles, result.ParseOptions.LanguageVersion.ToString(), result.ParseOptions.PreprocessorSymbolNames.ToArray(), result.TargetFramework, result.AssemblyName, result.ProjectReferences,
              result.LinkedCompileItems)
          { BuildInputs = result.BuildInputs, TargetPath = result.TargetPath, HasUnsafeGeneratedCompileInputs = result.HasUnsafeGeneratedCompileInputs }, ct);
    }
    return result;
  }

  private static ProjectEvaluation ParseProjectEvaluation(string output, string project, string? targetFramework)
  {
    using var doc = JsonDocument.Parse(output);
    var root = doc.RootElement;
    var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project))!;
    var compileItems = root.GetProperty("Items").GetProperty("Compile").EnumerateArray().ToArray();
    var unsafeGeneratedCompileItem = compileItems
        .Select(item => item.TryGetProperty("FullPath", out var fullPath) && !string.IsNullOrWhiteSpace(fullPath.GetString())
            ? Path.GetFullPath(fullPath.GetString()!)
            : Path.GetFullPath(Path.Combine(projectDirectory, item.GetProperty("Identity").GetString()!)))
        .FirstOrDefault(file => file.Split(Path.DirectorySeparatorChar).Contains("bin", StringComparer.OrdinalIgnoreCase) ||
            file.Split(Path.DirectorySeparatorChar).Contains("obj", StringComparer.OrdinalIgnoreCase) && !BuildInputs.IsKnownSdkGeneratedCompileSource(file));
    var buildInputs = new[] { "None", "Content", "EmbeddedResource", "AdditionalFiles", "Analyzer", "Page", "Resource" }
        .Where(itemName => root.GetProperty("Items").TryGetProperty(itemName, out _))
        .SelectMany(itemName => root.GetProperty("Items").GetProperty(itemName).EnumerateArray())
        .Where(item => item.TryGetProperty("FullPath", out var fullPath) && File.Exists(fullPath.GetString()))
        .Select(item => Path.GetFullPath(item.GetProperty("FullPath").GetString()!))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var files = compileItems
        .Select(item => Path.GetFullPath(Path.Combine(projectDirectory, item.GetProperty("Identity").GetString()!)))
        .Where(File.Exists).Where(file => !file.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) && !file.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
    var linkedCompileItems = compileItems.Where(item => item.TryGetProperty("FullPath", out var fullPathElement) &&
            !string.IsNullOrWhiteSpace(fullPathElement.GetString()))
        .Select(item => new
        {
          File = Path.GetFullPath(item.GetProperty("FullPath").GetString()!),
          Identity = item.GetProperty("Identity").GetString()!,
          DefiningProjectPath = item.TryGetProperty("DefiningProjectFullPath", out var definingProject) ? definingProject.GetString() : null
        })
        .Where(item => File.Exists(item.File) && !PathSafety.IsWithinDirectory(item.File, projectDirectory))
        .Select(item => new LinkedCompileItem(item.File, item.Identity, item.DefiningProjectPath ?? Path.GetFullPath(project), Path.GetFullPath(project)))
        .OrderBy(item => item.File, StringComparer.OrdinalIgnoreCase).ToArray();
    var projectReferences = root.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
        .Where(item => !item.TryGetProperty("Metadata", out var metadata) || !metadata.TryGetProperty("ReferenceOutputAssembly", out var outputAssembly) ||
            !string.Equals(outputAssembly.GetString(), "false", StringComparison.OrdinalIgnoreCase))
        .Select(item => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, item.GetProperty("Identity").GetString()!)))
        .Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var properties = root.GetProperty("Properties");
    var language = properties.GetProperty("LangVersion").GetString();
    var defineConstants = properties.GetProperty("DefineConstants").GetString();
    var evaluatedTargetFramework = properties.GetProperty("TargetFramework").GetString();
    var targetFrameworks = properties.GetProperty("TargetFrameworks").GetString();
    var assemblyName = properties.GetProperty("AssemblyName").GetString();
    var targetPath = properties.GetProperty("TargetPath").GetString();
    if (string.IsNullOrWhiteSpace(targetFramework) && string.IsNullOrWhiteSpace(evaluatedTargetFramework))
    {
      var available = (targetFrameworks ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      if (available.Length > 1)
      {
        throw new InvalidOperationException($"Project '{project}' targets multiple frameworks. Select one with --framework ({string.Join(", ", available)}).");
      }

      if (available.Length == 1)
      {
        evaluatedTargetFramework = available[0];
      }
    }
    var version = LanguageVersion.Preview;
    if (!string.IsNullOrWhiteSpace(language) && !LanguageVersionFacts.TryParse(language, out version))
    {
      throw new InvalidOperationException($"MSBuild returned an unsupported C# language version '{language}'.");
    }

    var symbols = (defineConstants ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var resolvedAssemblyName = string.IsNullOrWhiteSpace(assemblyName) ? Path.GetFileNameWithoutExtension(project) : assemblyName;
    var parseOptions = new CSharpParseOptions(version, preprocessorSymbols: symbols);
    var result = new ProjectEvaluation(files, parseOptions, evaluatedTargetFramework,
        resolvedAssemblyName, projectReferences, [Path.GetFullPath(project)], files.ToDictionary(file => file, _ => resolvedAssemblyName, StringComparer.OrdinalIgnoreCase))
    {
      TargetPath = targetPath,
      LinkedCompileItems = linkedCompileItems,
      BuildInputs = buildInputs,
      HasUnsafeGeneratedCompileInputs = unsafeGeneratedCompileItem is not null,
      ParseOptionsByFile = files.ToDictionary(file => file, _ => parseOptions, StringComparer.OrdinalIgnoreCase)
    };
    return result;
  }

  internal static string ResolveEvaluatedTargetPath(ProjectEvaluation evaluation, string project, string? targetFramework)
  {
    if (!string.IsNullOrWhiteSpace(evaluation.TargetPath))
    {
      return Path.GetFullPath(evaluation.TargetPath);
    }

    if (string.IsNullOrWhiteSpace(targetFramework))
    {
      throw new InvalidOperationException($"MSBuild did not provide a target path or framework for '{project}'.");
    }

    return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, "bin", "Debug", targetFramework,
        evaluation.AssemblyName + ".dll");
  }

  internal static ProjectEvaluation CreateProjectEvaluation(CachedProjectEvaluation cached, string project)
  {
    if (!Enum.TryParse<LanguageVersion>(cached.LanguageVersion, true, out var version) || !Enum.IsDefined(version))
    {
      throw new InvalidDataException($"Cached project evaluation contains unknown language version '{cached.LanguageVersion}'.");
    }
    var parseOptions = new CSharpParseOptions(version, preprocessorSymbols: cached.DefineConstants);
    return new ProjectEvaluation(cached.CompileFiles, parseOptions, cached.TargetFramework,
        cached.AssemblyName, cached.ProjectReferences, [Path.GetFullPath(project)], cached.CompileFiles.ToDictionary(file => file, _ => cached.AssemblyName, StringComparer.OrdinalIgnoreCase))
    {
      TargetPath = cached.TargetPath,
      LinkedCompileItems = cached.LinkedCompileItems,
      BuildInputs = cached.BuildInputs,
      HasUnsafeGeneratedCompileInputs = cached.HasUnsafeGeneratedCompileInputs,
      ParseOptionsByFile = cached.CompileFiles.ToDictionary(file => file, _ => parseOptions, StringComparer.OrdinalIgnoreCase)
    };
  }

  internal static async Task<ProjectEvaluation> GetProjectGraph(string project, RunMetrics metrics, CancellationToken cancellationToken,
      string? requestedFramework, ArtifactCache cache, List<string> diagnostics)
  {
    var rootFramework = await SelectProjectFramework(project, requestedFramework, requestedFramework, true, metrics, cancellationToken, cache, diagnostics);
    var root = await GetCompileFiles(project, metrics, cancellationToken, rootFramework, cache, diagnostics);
    var files = new HashSet<string>(root.CompileFiles, StringComparer.OrdinalIgnoreCase);
    var linkedCompileItems = new List<LinkedCompileItem>(root.LinkedCompileItems);
    var buildInputs = new HashSet<string>(root.BuildInputs, StringComparer.OrdinalIgnoreCase);
    var hasUnsafeGeneratedCompileInputs = root.HasUnsafeGeneratedCompileInputs;
    var assemblyNames = new Dictionary<string, string>(root.AssemblyNamesByFile, StringComparer.OrdinalIgnoreCase);
    var parseOptionsByFile = new Dictionary<string, CSharpParseOptions>(root.ParseOptionsByFile, StringComparer.OrdinalIgnoreCase);
    var projects = new HashSet<string>([Path.GetFullPath(project)], StringComparer.OrdinalIgnoreCase);
    var pending = new Queue<string>(root.ProjectReferences);
    while (pending.TryDequeue(out var reference))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var fullReference = Path.GetFullPath(reference);
      if (!projects.Add(fullReference))
      {
        continue;
      }

      var framework = await SelectProjectFramework(fullReference, null, root.TargetFramework, false, metrics, cancellationToken, cache, diagnostics);
      var dependency = await GetCompileFiles(fullReference, metrics, cancellationToken, framework, cache, diagnostics);
      linkedCompileItems.AddRange(dependency.LinkedCompileItems);
      buildInputs.UnionWith(dependency.BuildInputs);
      hasUnsafeGeneratedCompileInputs |= dependency.HasUnsafeGeneratedCompileInputs;
      foreach (var file in dependency.CompileFiles)
      {
        files.Add(file);
        assemblyNames[file] = dependency.AssemblyName;
        parseOptionsByFile[file] = dependency.ParseOptionsByFile[file];
      }
      foreach (var transitiveReference in dependency.ProjectReferences)
      {
        pending.Enqueue(transitiveReference);
      }
    }
    return root with
    {
      CompileFiles = files.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      ProjectPaths = projects.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      AssemblyNamesByFile = assemblyNames,
      ParseOptionsByFile = parseOptionsByFile,
      LinkedCompileItems = linkedCompileItems.DistinctBy(item => (item.File, item.DefiningProjectPath, item.Identity)).ToArray(),
      BuildInputs = buildInputs.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      HasUnsafeGeneratedCompileInputs = hasUnsafeGeneratedCompileInputs
    };
  }

  internal static async Task<ProjectEvaluation> GetProductionGraph(string primaryProject, IReadOnlyList<string> additionalProjects,
      RunMetrics metrics, CancellationToken cancellationToken, string? requestedFramework, ArtifactCache cache, List<string> diagnostics)
  {
    var projects = new[] { primaryProject }.Concat(additionalProjects).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var graphs = new List<ProjectEvaluation>(projects.Length);
    foreach (var project in projects)
    {
      graphs.Add(await GetProjectGraph(project, metrics, cancellationToken, requestedFramework, cache, diagnostics));
    }
    var files = graphs.SelectMany(graph => graph.CompileFiles).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    var assemblyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var parseOptionsByFile = new Dictionary<string, CSharpParseOptions>(StringComparer.OrdinalIgnoreCase);
    foreach (var graph in graphs)
    {
      foreach (var mapping in graph.AssemblyNamesByFile)
      {
        assemblyNames[mapping.Key] = mapping.Value;
      }
      foreach (var mapping in graph.ParseOptionsByFile)
      {
        parseOptionsByFile[mapping.Key] = mapping.Value;
      }
    }
    return graphs[0] with
    {
      CompileFiles = files,
      ProjectPaths = graphs.SelectMany(graph => graph.ProjectPaths).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      AssemblyNamesByFile = assemblyNames,
      ParseOptionsByFile = parseOptionsByFile,
      LinkedCompileItems = graphs.SelectMany(graph => graph.LinkedCompileItems)
          .DistinctBy(item => (item.File, item.DefiningProjectPath, item.Identity)).ToArray(),
      BuildInputs = graphs.SelectMany(graph => graph.BuildInputs).Distinct(StringComparer.OrdinalIgnoreCase)
          .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      HasUnsafeGeneratedCompileInputs = graphs.Any(graph => graph.HasUnsafeGeneratedCompileInputs)
    };
  }

  internal static async Task<string?> SelectProjectFramework(string project, string? requestedFramework, string? rootFramework,
      bool isRoot, RunMetrics metrics, CancellationToken cancellationToken, ArtifactCache? cache = null, List<string>? diagnostics = null)
  {
    var projectPath = Path.GetFullPath(project);
    var inputs = BuildInputs.ProjectConfigurationInputs(projectPath, out var unresolvedImport);
    var cacheable = cache is not null && unresolvedImport is null;
    var key = ArtifactCache.ContentKey(inputs,
        ["project-framework-selection-v1", projectPath, CacheIdentity.EnvironmentFingerprint(), CacheIdentity.EngineVersion()]);
    CachedTargetFrameworkProperties? evaluated = null;
    var cacheHit = false;
    if (cacheable)
    {
      var cacheRead = await cache!.TryReadAsync<CachedTargetFrameworkProperties>("project-framework-selection", projectPath,
          key, cancellationToken);
      diagnostics?.Add(cacheRead.Diagnostic);
      evaluated = cacheRead.Value;
      cacheHit = cacheRead.Found;
    }
    else if (cache is not null)
    {
      diagnostics?.Add($"CacheBypass: project framework selection has an unresolved import ({unresolvedImport}).");
    }

    if (!cacheHit)
    {
      var args = new[] { "msbuild", projectPath, "-getProperty:TargetFramework,TargetFrameworks", "-nologo" };
      var process = await ProcessRunner.RunProcess("dotnet", args, Path.GetDirectoryName(projectPath)!, null, 60, metrics, cancellationToken);
      if (process.ExitCode != 0)
      {
        throw new InvalidOperationException($"Unable to evaluate target frameworks for '{project}'. {process.Output}");
      }

      using var document = JsonDocument.Parse(process.Output);
      var properties = document.RootElement.GetProperty("Properties");
      evaluated = new CachedTargetFrameworkProperties(properties.GetProperty("TargetFramework").GetString(),
          properties.GetProperty("TargetFrameworks").GetString());
      if (cacheable)
      {
        await cache!.WriteAsync("project-framework-selection", projectPath, key, evaluated, cancellationToken);
      }
    }

    var single = evaluated!.TargetFramework;
    var frameworks = (evaluated.TargetFrameworks ?? string.Empty)
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (frameworks.Length > 1)
    {
      var selected = requestedFramework ?? rootFramework;
      if (string.IsNullOrWhiteSpace(selected))
      {
        throw new InvalidOperationException($"Project '{project}' targets multiple frameworks. Select one with --framework ({string.Join(", ", frameworks)}).");
      }
      if (!frameworks.Contains(selected, StringComparer.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException($"Selected framework '{selected}' is not supported by project '{project}' ({string.Join(", ", frameworks)}).");
      }
      return selected;
    }
    if (isRoot && !string.IsNullOrWhiteSpace(requestedFramework) && !string.IsNullOrWhiteSpace(single) &&
        !string.Equals(requestedFramework, single, StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException($"Selected framework '{requestedFramework}' does not match project '{project}' target '{single}'.");
    }
    return null;
  }

  internal static string FindTestAssembly(string? evaluatedTargetPath, string originalProject, string assemblyName,
      string? targetFramework = null, string? clonedProject = null)
  {
    string? assembly = null;
    if (!string.IsNullOrWhiteSpace(evaluatedTargetPath))
    {
      var fullTargetPath = Path.GetFullPath(evaluatedTargetPath);
      if (clonedProject is null)
      {
        assembly = fullTargetPath;
      }
      else
      {
        var relativeTargetPath = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(originalProject))!, fullTargetPath);
        assembly = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clonedProject))!, relativeTargetPath));
      }
    }

    var framework = targetFramework;
    if (string.IsNullOrWhiteSpace(assembly) || !File.Exists(assembly))
    {
      if (string.IsNullOrWhiteSpace(framework))
      {
        throw new InvalidOperationException($"MSBuild did not provide a usable TargetPath for test project '{originalProject}'.");
      }

      var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(clonedProject ?? originalProject))!;
      assembly = Path.Combine(projectDirectory, "bin", "Debug", framework, assemblyName + ".dll");
    }

    return File.Exists(assembly) ? assembly : throw new FileNotFoundException("Built test assembly not found at the MSBuild-evaluated target path.", assembly);
  }
}
