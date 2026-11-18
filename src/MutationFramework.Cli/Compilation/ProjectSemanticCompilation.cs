using System.Text;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace MutationFramework.Sdk;

internal sealed record GeneratedSemanticSource(string Path, string Text);
internal sealed record CompilerInvocation(string Project, string TargetFramework, string[] Arguments, GeneratedSemanticSource[] GeneratedSources);
internal sealed record CompilerInputDependencies(string[] Files);
internal sealed record SemanticCompilationArtifact(CompilerInvocation[] Invocations, string[] DependencyFiles, string DependencyHash);
internal sealed record ProjectSemanticCompilation(string Project, string TargetFramework, string AssemblyName,
    string[] SourceFiles, CSharpParseOptions ParseOptions, Lazy<CSharpCompilation> BoundCompilation, string Fingerprint)
{
  internal CSharpCompilation Compilation => BoundCompilation.Value;
}

internal static class ProjectSemanticCompilations
{
  internal static async Task<IReadOnlyList<ProjectSemanticCompilation>> LoadAsync(string project, ProjectEvaluation evaluation,
      ArtifactCache cache, List<string> diagnostics, CancellationToken ct, RunMetrics? runMetrics = null)
  {
    var metrics = runMetrics ?? new RunMetrics();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    using var outputLocks = await CompilationArtifactCache.AcquireCompilationLocksAsync(evaluation.ProjectPaths, 120, ct);
    var key = CompilationArtifactCache.GetCompilationArtifactKey(project, evaluation, evaluation.TargetFramework,
        out var cacheable, out var bypassReason);
    var identity = Path.GetFullPath(project) + "|" + evaluation.TargetFramework;
    SemanticCompilationArtifact? artifact = null;
    if (cacheable)
    {
      var dependencies = await cache.TryReadAsync<CompilerInputDependencies>("semantic-input-dependencies-v1", identity, key, ct);
      if (!dependencies.Found || dependencies.Value?.Files is { Length: > 0 })
      {
        diagnostics.Add(dependencies.Diagnostic);
      }
      if (dependencies.Found && dependencies.Value?.Files is { Length: > 0 })
      {
        var dependencyKey = ArtifactCache.ContentKey(dependencies.Value!.Files, []);
        var semanticKey = ArtifactCache.ContentKey([], ["semantic-compilation-v3", key, dependencyKey]);
        var read = await cache.TryReadAsync<SemanticCompilationArtifact>("semantic-compilation-v3", identity, semanticKey, ct);
        if (!read.Found)
        {
          diagnostics.Add(read.Diagnostic);
        }
        if (read.Found && read.Value is { Invocations.Length: > 0, DependencyFiles: not null } value &&
            value.DependencyHash == dependencyKey && value.DependencyFiles.All(File.Exists) &&
            value.Invocations.All(invocation => invocation is { Arguments.Length: > 0, GeneratedSources: not null } &&
                evaluation.ProjectPaths.Contains(invocation.Project, StringComparer.OrdinalIgnoreCase)))
        {
          artifact = value;
          diagnostics.Add(read.Diagnostic);
        }
        else if (read.Found)
        {
          diagnostics.Add("CacheInvalidated: semantic-compilation-v3 invalid compiler context metadata.");
        }
      }
      else
      {
        diagnostics.Add(dependencies.Found || dependencies.Diagnostic.StartsWith("CacheInvalidated:", StringComparison.Ordinal)
            ? "CacheInvalidated: semantic-compilation-v3 compiler input dependency set changed."
            : "CacheMiss: semantic-compilation-v3 compiler input dependencies are not known yet.");
      }
    }
    else
    {
      diagnostics.Add("CacheBypass: semantic compilation: " + bypassReason);
    }

    if (artifact is null)
    {
      // Use the actual compiler task, not an approximation of project evaluation. This also
      // executes custom compile targets and generators in the SDK's own compiler host.
      var invocations = new List<CompilerInvocation>();
      var order = CompilationOrder(evaluation);
      var referenced = evaluation.ReferencesByProject.Values.SelectMany(paths => paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach (var root in order.Where(path => !referenced.Contains(path)))
      {
        var restore = await ProcessRunner.RunProcess("dotnet", ["restore", root, "--nologo", "--verbosity", "quiet"],
            Path.GetDirectoryName(root)!, null, 120, metrics, ct);
        if (restore.ExitCode != 0)
        {
          throw new InvalidOperationException($"Semantic compilation restore failed for '{root}': {restore.Output}");
        }
      }
      foreach (var path in order)
      {
        var directory = Path.GetDirectoryName(path)!;
        var generatedDirectory = Path.GetFullPath(Path.Combine(directory, "obj", "MutagenSemanticGenerated"));
        if (!PathSafety.IsWithinDirectory(generatedDirectory, Path.Combine(directory, "obj")))
        {
          throw new InvalidOperationException("Semantic generator output must remain inside the owning project's obj directory.");
        }
        if (Directory.Exists(generatedDirectory))
        {
          Directory.Delete(generatedDirectory, true);
        }
        var args = new List<string> { "msbuild", path, "-t:Rebuild", "-nodeReuse:false", "-nologo", "-verbosity:quiet",
          "-p:Configuration=Debug", "-p:BuildProjectReferences=false", "-p:UseSharedCompilation=false", "-p:ProvideCommandLineArgs=true", "-p:EmitCompilerGeneratedFiles=true",
          "-p:CompilerGeneratedFilesOutputPath=obj/MutagenSemanticGenerated", "-getItem:CscCommandLineArgs", "-getProperty:TargetFramework" };
        var framework = await CompilationWorkspace.SelectProjectFramework(path, null, evaluation.TargetFrameworksByProject.GetValueOrDefault(path) ?? evaluation.TargetFramework, false, metrics, ct, cache, diagnostics);
        if (!string.IsNullOrWhiteSpace(framework))
        {
          args.Add("-p:TargetFramework=" + framework);
        }
        var build = await ProcessRunner.RunProcess("dotnet", args, directory,
            new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1", ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" }, 120, metrics, ct);
        if (build.ExitCode != 0)
        {
          var errors = string.Join(Environment.NewLine, build.Output.Split('\n').Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase)));
          if (string.IsNullOrWhiteSpace(errors))
          {
            errors = build.Output.Length <= 4000 ? build.Output : build.Output[^4000..];
          }
          throw new InvalidOperationException($"Unable to obtain the actual compiler invocation for '{path}': {errors}");
        }
        var jsonStart = build.Output.IndexOf('{');
        if (jsonStart < 0)
        {
          throw new InvalidOperationException($"MSBuild returned no compiler inputs for '{path}'.");
        }
        using var document = ParseBuildOutput(build.Output[jsonStart..]);
        var arguments = document.RootElement.GetProperty("Items").GetProperty("CscCommandLineArgs")
            .EnumerateArray().Select(item => item.GetProperty("Identity").GetString()!).ToArray();
        if (arguments.Length == 0)
        {
          throw new InvalidOperationException($"Project '{path}' did not expose a C# compiler invocation.");
        }
        var generated = Directory.Exists(generatedDirectory)
            ? Directory.GetFiles(generatedDirectory, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => new GeneratedSemanticSource(file, File.ReadAllText(file))).ToArray() : [];
        invocations.Add(new(path, document.RootElement.GetProperty("Properties").GetProperty("TargetFramework").GetString()!, arguments, generated));
      }
      var dependencies = invocations.SelectMany(invocation =>
      {
        var parsed = Parse(invocation);
        return parsed.MetadataReferences.Select(reference => Path.GetFullPath(reference.Reference, Path.GetDirectoryName(invocation.Project)!))
            .Concat(parsed.AnalyzerReferences.Select(reference => Path.GetFullPath(reference.FilePath, Path.GetDirectoryName(invocation.Project)!)))
            .Concat(parsed.SourceFiles.Select(source => Path.GetFullPath(source.Path, Path.GetDirectoryName(invocation.Project)!)))
            .Concat(parsed.AdditionalFiles.Select(source => source.Path))
            .Concat(parsed.AnalyzerConfigPaths)
            .Concat(new[] { parsed.AppConfigPath, parsed.CompilationOptions.CryptoKeyFile }
                .Where(file => !string.IsNullOrWhiteSpace(file)).Select(file => Path.GetFullPath(file!, Path.GetDirectoryName(invocation.Project)!)));
      }).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
      artifact = new(invocations.ToArray(), dependencies, ArtifactCache.ContentKey(dependencies, []));
      // SDK-generated inputs may have changed during the build. Store against the final state.
      key = CompilationArtifactCache.GetCompilationArtifactKey(project, evaluation, evaluation.TargetFramework, out cacheable, out _);
      if (cacheable)
      {
        await cache.WriteAsync("semantic-input-dependencies-v1", identity, key, new CompilerInputDependencies(dependencies), ct);
        var semanticKey = ArtifactCache.ContentKey([], ["semantic-compilation-v3", key, artifact.DependencyHash]);
        await cache.WriteAsync("semantic-compilation-v3", identity, semanticKey, artifact, ct);
      }
    }

    var referenceSnapshots = new Dictionary<(string Path, MetadataReferenceProperties Properties), PortableExecutableReference>();
    var results = new List<ProjectSemanticCompilation>();
    foreach (var invocation in artifact.Invocations)
    {
      ct.ThrowIfCancellationRequested();
      var parsed = Parse(invocation);
      var errors = parsed.Errors.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      if (errors.Length != 0)
      {
        throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      }
      // Snapshot mutable build outputs while holding the graph locks. Parsing and binding
      // remain lazy so a validated candidate-cache hit does not rebind the entire solution.
      var sources = parsed.SourceFiles.Select(source =>
      {
        var sourcePath = Path.GetFullPath(source.Path, Path.GetDirectoryName(invocation.Project)!);
        using var stream = File.OpenRead(sourcePath);
        return (File: sourcePath, Text: SourceText.From(stream, parsed.Encoding ?? Encoding.UTF8));
      }).ToList();
      var paths = sources.Select(source => source.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
      sources.AddRange(invocation.GeneratedSources.Where(source => !paths.Contains(source.Path))
          .Select(source => (source.Path, SourceText.From(source.Text, Encoding.UTF8))));
      var references = parsed.MetadataReferences.Select(reference =>
      {
        var path = Path.GetFullPath(reference.Reference, Path.GetDirectoryName(invocation.Project)!);
        var referenceKey = (path, reference.Properties);
        if (!referenceSnapshots.TryGetValue(referenceKey, out var snapshot))
        {
          snapshot = MetadataReference.CreateFromImage(System.Collections.Immutable.ImmutableArray.CreateRange(File.ReadAllBytes(path)),
              reference.Properties, filePath: path);
          referenceSnapshots.Add(referenceKey, snapshot);
        }
        return snapshot;
      }).ToArray();
      var comparer = DesktopAssemblyIdentityComparer.Default;
      if (parsed.AppConfigPath is not null)
      {
        using var config = File.OpenRead(parsed.AppConfigPath);
        comparer = DesktopAssemblyIdentityComparer.LoadFromXml(config);
      }
      var options = parsed.CompilationOptions.WithStrongNameProvider(new DesktopStrongNameProvider(parsed.KeyFileSearchPaths))
          .WithAssemblyIdentityComparer(comparer)
          .WithSourceReferenceResolver(new SourceFileResolver([], Path.GetDirectoryName(invocation.Project)!, parsed.PathMap))
          .WithXmlReferenceResolver(new XmlFileResolver(Path.GetDirectoryName(invocation.Project)!));
      var assemblyName = parsed.CompilationName ?? Path.GetFileNameWithoutExtension(parsed.OutputFileName)!;
      var compilation = new Lazy<CSharpCompilation>(() =>
      {
        var bindingTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
          var trees = sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, parsed.ParseOptions, source.File, ct));
          var bound = CSharpCompilation.Create(assemblyName, trees, references, options);
          var bindingErrors = bound.GetDiagnostics(ct).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
          if (bindingErrors.Length != 0)
          {
            throw new InvalidOperationException($"The semantic compilation for '{invocation.Project}' differs from its successful build: " +
                string.Join(Environment.NewLine, bindingErrors.Select(error => error.ToString())));
          }
          return bound;
        }
        finally { Interlocked.Add(ref metrics.SemanticAnalysisMs, bindingTimer.ElapsedMilliseconds); }
      });
      results.Add(new(invocation.Project, invocation.TargetFramework, assemblyName, sources.Select(source => source.File).ToArray(),
          parsed.ParseOptions, compilation, CacheIdentity.Hash(artifact.DependencyHash + string.Join("\n", invocation.Arguments) +
              string.Join("\n", invocation.GeneratedSources.Select(source => source.Text)))));
    }
    Interlocked.Add(ref metrics.SemanticAnalysisMs, timer.ElapsedMilliseconds);
    return results;
  }

  private static List<string> CompilationOrder(ProjectEvaluation evaluation)
  {
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var order = new List<string>();
    void Visit(string project)
    {
      if (visited.Contains(project))
      {
        return;
      }
      if (!active.Add(project))
      {
        throw new InvalidOperationException("Cyclic project references cannot be compiled: " + project);
      }
      foreach (var dependency in (evaluation.ReferencesByProject.GetValueOrDefault(project) ?? []).Order(StringComparer.OrdinalIgnoreCase))
      {
        Visit(dependency);
      }
      active.Remove(project);
      visited.Add(project);
      order.Add(project);
    }
    foreach (var project in evaluation.ProjectPaths.Order(StringComparer.OrdinalIgnoreCase))
    {
      Visit(project);
    }
    return order;
  }

  internal static ProjectEvaluation ApplyInputs(ProjectEvaluation evaluation, IReadOnlyList<ProjectSemanticCompilation> compilations)
  {
    var production = evaluation.SourceOwnership.Select(owner => owner.Project).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var owners = compilations.Where(owner => production.Contains(owner.Project)).Select(owner => new ProjectSourceOwnership(owner.Project,
        owner.SourceFiles.Where(file => !file.Split(Path.DirectorySeparatorChar)
            .Any(part => part.Equals("obj", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            .ToArray(), owner.ParseOptions, owner.TargetFramework)).ToArray();
    if (owners.Length == 0)
    {
      return evaluation;
    }
    var assemblyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var parseOptions = new Dictionary<string, CSharpParseOptions>(StringComparer.OrdinalIgnoreCase);
    foreach (var owner in owners)
    {
      var assembly = compilations.Single(compilation => compilation.Project == owner.Project).AssemblyName;
      foreach (var file in owner.Files)
      {
        assemblyNames[file] = assembly;
        parseOptions[file] = owner.ParseOptions;
      }
    }
    return evaluation with
    {
      CompileFiles = owners.SelectMany(owner => owner.Files).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
      SourceOwnership = owners,
      AssemblyNamesByFile = assemblyNames,
      ParseOptionsByFile = parseOptions
    };
  }

  private static JsonDocument ParseBuildOutput(string json)
  {
    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
    return JsonDocument.ParseValue(ref reader);
  }

  private static CSharpCommandLineArguments Parse(CompilerInvocation invocation) =>
      CSharpCommandLineParser.Default.Parse(invocation.Arguments, Path.GetDirectoryName(invocation.Project)!, sdkDirectory: null);
}
