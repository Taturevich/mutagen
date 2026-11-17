using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed record CachedCompilationArtifacts(CachedCompilationFile[] Files, string IntegrityHash);


internal sealed record CachedCompilationFile(string RelativePath, string BlobKey);


internal static class CompilationArtifactCache
{
  internal static async Task<FileStream> AcquireCompilationOutputLockAsync(string outputDirectory, int timeoutSeconds, CancellationToken cancellationToken)
  {
    var lockDirectory = Path.Combine(Path.GetTempPath(), "mutation-framework", "compilation-locks");
    Directory.CreateDirectory(lockDirectory);
    var lockPath = Path.Combine(lockDirectory, CacheIdentity.Hash(Path.GetFullPath(outputDirectory).ToUpperInvariant()) + ".lock");
    var timer = Stopwatch.StartNew();
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      }
      catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
      {
        await Task.Delay(50, cancellationToken);
      }

      if (timer.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
      {
        throw new TimeoutException($"Timed out waiting to access compilation output '{outputDirectory}'.");
      }
    }
  }

  internal static async Task<IDisposable> AcquireCompilationLocksAsync(IEnumerable<string> identities, int timeoutSeconds,
      CancellationToken cancellationToken)
  {
    var locks = new List<FileStream>();
    try
    {
      foreach (var identity in identities.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                   .Order(StringComparer.OrdinalIgnoreCase))
      {
        locks.Add(await AcquireCompilationOutputLockAsync(identity, timeoutSeconds, cancellationToken));
      }

      return new CompilationLockSet(locks);
    }
    catch
    {
      for (var index = locks.Count - 1; index >= 0; index--)
      {
        locks[index].Dispose();
      }

      throw;
    }
  }

  internal static string GetCompilationArtifactKey(string project, ProjectEvaluation evaluation, string? targetFramework,
      out bool canCache, out string? bypassReason)
  {
    canCache = !evaluation.HasUnsafeGeneratedCompileInputs;
    bypassReason = evaluation.HasUnsafeGeneratedCompileInputs ? "project graph contains generated Compile items outside the cacheable SDK set" : null;
    var inputs = new HashSet<string>(evaluation.CompileFiles, StringComparer.OrdinalIgnoreCase);
    inputs.UnionWith(evaluation.BuildInputs);
    foreach (var evaluatedProject in evaluation.ProjectPaths)
    {
      var configurationInputs = BuildInputs.ProjectConfigurationInputs(evaluatedProject, out var unresolvedImport);
      inputs.UnionWith(configurationInputs);
      inputs.UnionWith(BuildInputs.ProjectBuildContentInputs(evaluatedProject));
      inputs.UnionWith(BuildInputs.GeneratedCompileInputs(evaluatedProject, out var unsafeGeneratedSource));
      if (unsafeGeneratedSource is not null)
      {
        canCache = false;
        bypassReason ??= unsafeGeneratedSource;
      }
      var unsafeCodeGenerationTarget = BuildInputs.FindUnsafeCodeGenerationTarget(configurationInputs);
      if (unsafeCodeGenerationTarget is not null)
      {
        canCache = false;
        bypassReason ??= $"custom target may generate C# from untracked inputs ({unsafeCodeGenerationTarget})";
      }
      if (unresolvedImport is not null)
      {
        canCache = false;
        bypassReason ??= $"unresolved project import {unresolvedImport}";
      }

      var assetsFile = Path.Combine(Path.GetDirectoryName(evaluatedProject)!, "obj", "project.assets.json");
      if (!File.Exists(assetsFile))
      {
        canCache = false;
        bypassReason ??= $"restore assets are missing for {evaluatedProject}";
        continue;
      }

      try
      {
        var packageInputs = BuildInputs.PackageAssetInputs(assetsFile);
        inputs.UnionWith(packageInputs);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
      {
        canCache = false;
        bypassReason ??= $"restore assets cannot be inspected for {evaluatedProject}: {exception.Message}";
      }
    }

    var values = new List<string>
    {
      "compilation-artifacts-v1", Path.GetFullPath(project), evaluation.AssemblyName, targetFramework ?? string.Empty,
      "Configuration=Debug", CacheIdentity.EnvironmentFingerprint(), CacheIdentity.EngineVersion(),
      string.Join(";", evaluation.ProjectPaths.Order(StringComparer.OrdinalIgnoreCase)),
      string.Join(";", evaluation.ParseOptionsByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
          .Select(pair => pair.Key + "=" + pair.Value.LanguageVersion + ":" + string.Join(",", pair.Value.PreprocessorSymbolNames.Order(StringComparer.Ordinal))))
    };
    return ArtifactCache.ContentKey(inputs, values);
  }

  internal static string GetMutationCompilationArtifactKey(string project, ProjectEvaluation evaluation, string? targetFramework,
      IReadOnlyList<Candidate> candidates, MutationOptions options, out bool canCache, out string? bypassReason)
  {
    var projectKey = GetCompilationArtifactKey(project, evaluation, targetFramework, out canCache, out bypassReason);
    if (!canCache)
    {
      return projectKey;
    }

    var mutatorFingerprint = CacheIdentity.MutatorFingerprint(options);
    var candidateFingerprint = candidates.OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
        .Select(candidate => string.Join("|", candidate.Id, candidate.SiteId, candidate.Operator, candidate.File,
            candidate.Start, candidate.Length, candidate.Original, candidate.Replacement, candidate.ReplacementToken,
            candidate.SiteRuntimeId, candidate.MutantRuntimeId, candidate.ApplicationKind));
    return ArtifactCache.ContentKey([], ["mutation-compilation-artifacts-v1", projectKey, mutatorFingerprint,
        options.Include ?? string.Empty, options.Exclude ?? string.Empty,
        string.Join("\n", candidateFingerprint), MutationRuntimeSource.RuntimeSource(candidates.Select(candidate => candidate.SiteRuntimeId)
            .Distinct(StringComparer.Ordinal).Count()), typeof(MutationEngine).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
        CacheIdentity.HostVersion()]);
  }

  internal static async Task<(bool Restored, string Diagnostic)> TryRestoreCompilationArtifacts(ArtifactCache cache, string phase, string logicalIdentity,
      string contentKey, string outputDirectory, bool privateOutputDirectory, CancellationToken cancellationToken)
  {
    if (!privateOutputDirectory)
    {
      return (false, $"CacheBypass: {phase} output restore skipped because '{outputDirectory}' is shared with external build tools.");
    }

    var cacheRead = await cache.TryReadAsync<CachedCompilationArtifacts>(phase, logicalIdentity, contentKey,
        cancellationToken);
    if (!cacheRead.Found)
    {
      return (false, cacheRead.Diagnostic);
    }

    var artifact = cacheRead.Value;

    if (artifact?.Files is null || artifact.Files.Length == 0)
    {
      return (false, $"CacheInvalidated: {phase} {contentKey} has an empty output manifest");
    }

    var outputRoot = Path.GetFullPath(outputDirectory);
    foreach (var file in artifact.Files)
    {
      if (string.IsNullOrWhiteSpace(file.RelativePath))
      {
        return (false, $"CacheInvalidated: {phase} manifest contains an empty output path");
      }

      var destination = Path.GetFullPath(Path.Combine(outputRoot, file.RelativePath));
      if (!PathSafety.IsWithinDirectory(destination, outputRoot))
      {
        return (false, $"CacheInvalidated: {phase} manifest contains an unsafe path '{file.RelativePath}'");
      }
    }

    if (!string.Equals(artifact.IntegrityHash, CompilationArtifactsIntegrity(artifact.Files), StringComparison.Ordinal))
    {
      return (false, $"CacheInvalidated: {phase} output manifest integrity check failed");
    }

    var stagingRoot = outputRoot + ".mutagen-restore-" + Guid.NewGuid().ToString("N");
    try
    {
      foreach (var file in artifact.Files)
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(file.RelativePath))
        {
          return (false, $"CacheInvalidated: {phase} manifest contains an empty output path");
        }

        var destination = Path.GetFullPath(Path.Combine(stagingRoot, file.RelativePath));
        if (!PathSafety.IsWithinDirectory(destination, stagingRoot))
        {
          return (false, $"CacheInvalidated: {phase} manifest contains an unsafe path '{file.RelativePath}'");
        }

        if (!await cache.TryCopyBlobAsync(phase, file.BlobKey, destination, cancellationToken))
        {
          return (false, $"CacheInvalidated: {phase} blob '{file.BlobKey}' is missing or corrupt");
        }
      }

      cancellationToken.ThrowIfCancellationRequested();
      if (Directory.Exists(outputRoot))
      {
        Directory.Delete(outputRoot, true);
      }

      Directory.CreateDirectory(Path.GetDirectoryName(outputRoot)!);
      Directory.Move(stagingRoot, outputRoot);
      return (true, $"CacheHit: {phase} {contentKey}");
    }
    finally
    {
      if (Directory.Exists(stagingRoot))
      {
        Directory.Delete(stagingRoot, true);
      }
    }
  }

  internal static async Task StoreCompilationArtifacts(ArtifactCache cache, string phase, string logicalIdentity, string contentKey, string outputDirectory,
      CancellationToken cancellationToken)
  {
    if (!Directory.Exists(outputDirectory))
    {
      return;
    }

    var outputRoot = Path.GetFullPath(outputDirectory);
    var files = new List<CachedCompilationFile>();
    foreach (var path in Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (phase.Equals("mutation-compilation-artifacts", StringComparison.Ordinal) &&
          Path.GetExtension(path).Equals(".pdb", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      files.Add(new(Path.GetRelativePath(outputRoot, path), await cache.StoreBlobAsync(phase, path, cancellationToken)));
    }

    var artifacts = files.ToArray();
    await cache.WriteAsync(phase, logicalIdentity, contentKey,
        new CachedCompilationArtifacts(artifacts, CompilationArtifactsIntegrity(artifacts)), cancellationToken);
  }

  internal static string CompilationArtifactsIntegrity(IEnumerable<CachedCompilationFile> files) =>
      ArtifactCache.ContentKey([], ["compilation-artifact-manifest-v1", .. files
          .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
          .Select(file => file.RelativePath + "=" + file.BlobKey)]);

  private sealed class CompilationLockSet(IReadOnlyList<FileStream> locks) : IDisposable
  {
    public void Dispose()
    {
      for (var index = locks.Count - 1; index >= 0; index--)
      {
        locks[index].Dispose();
      }
    }
  }
}
