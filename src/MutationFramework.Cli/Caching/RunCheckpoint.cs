using System.Text.Json;

namespace MutationFramework.Sdk;

internal sealed record CheckpointResults(MutationResult[] Results);

/// <summary>Durable completed execution units; a fresh successful baseline validates every resume.</summary>
internal sealed class RunCheckpoint(ArtifactCache cache, string fingerprint, IReadOnlyDictionary<string, string[]> units,
    IReadOnlyDictionary<string, string> unitScopes)
{
  internal Dictionary<string, MutationResult> Restored { get; } = new(StringComparer.Ordinal);

  internal static async Task<RunCheckpoint?> PrepareAsync(MutationProjects projects, PreparedWorkspace workspace, CompiledMutations compilation,
      IReadOnlyList<Candidate> candidates, BaselineCoverage baseline, PlannedMutations plan, MutationOptions options,
      List<string> diagnostics, CancellationToken cancellationToken)
  {
    if (options.CheckpointDirectory is null)
    {
      return null;
    }
    if (!plan.CoverageComplete)
    {
      throw new InvalidOperationException("Checkpoint reuse requires complete baseline coverage.");
    }
    var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var project in projects.Production.ProjectPaths.Concat(projects.TestEvaluations.SelectMany(evaluation => evaluation.ProjectPaths)).Distinct(StringComparer.OrdinalIgnoreCase))
    {
      inputs.UnionWith(BuildInputs.ProjectConfigurationInputs(project, out var unresolved));
      if (unresolved is not null)
      {
        throw new InvalidOperationException("Cannot checkpoint unresolved build imports: " + unresolved);
      }
      var clonedProject = Path.Combine(workspace.Root, Path.GetRelativePath(workspace.SolutionRoot, project));
      var assets = Path.Combine(Path.GetDirectoryName(clonedProject)!, "obj", "project.assets.json");
      // Fingerprint resolved package files, not clone-specific restore metadata paths.
      inputs.UnionWith(BuildInputs.PackageAssetInputs(assets));
    }
    inputs.UnionWith(projects.Production.CompileFiles);
    inputs.UnionWith(projects.Production.BuildInputs);
    foreach (var evaluation in projects.TestEvaluations)
    {
      inputs.UnionWith(evaluation.CompileFiles);
      inputs.UnionWith(evaluation.BuildInputs);
    }
    var values = new List<string> { "run-checkpoint-v2", options.CheckpointScopeKey!, CacheIdentity.EngineVersion(), CacheIdentity.HostVersion(),
      CacheIdentity.MutatorFingerprint(options), CacheIdentity.EnvironmentFingerprint(), options.TestHostIsolationPolicy.ToString(),
      options.ExecutionMode.ToString(), options.TimeoutSeconds.ToString(), options.MaxRunTimeoutSeconds.ToString(),
      options.HostStartupTimeoutSeconds.ToString(), options.PrioritizeFastTests.ToString(), options.TestFilter ?? "" };
    values.AddRange(candidates.OrderBy(candidate => candidate.Id).Select(candidate => candidate.Id + "|" + candidate.SiteId + "|" + candidate.Original + "|" + candidate.Replacement));
    values.AddRange(options.ResourceLocksByTestId.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + string.Join(",", pair.Value.Order(StringComparer.Ordinal))));
    var runtimeDependencies = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var assembly in compilation.TestDlls)
    {
      var directory = Path.GetDirectoryName(assembly)!;
      foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(BuildInputs.IsStableTestDiscoveryInput)
          .Where(CacheIdentity.IsRuntimeDependencyFile).Order(StringComparer.OrdinalIgnoreCase))
      {
        var name = Path.GetFileName(assembly) + "|" + Path.GetRelativePath(directory, file);
        var hash = RuntimeDependencyHash(file, workspace.Root);
        runtimeDependencies[name] = hash;
        values.Add(name + "|" + hash);
      }
    }
    var fingerprint = ArtifactCache.ContentKey(inputs, values);
    var work = ResourceAwareScheduler.Create(plan, baseline, options, new Dictionary<string, MutationResult>(), true, []).WorkUnits;
    var units = options.TestHostIsolationPolicy is TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup
        ? work.Select(batch => batch.MutantIds.ToArray()).ToArray()
        : work.SelectMany(batch => batch.MutantIds.Select(id => new[] { id })).ToArray();
    var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var batch in work)
    {
      var batchUnits = options.TestHostIsolationPolicy is TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup
          ? new[] { batch.MutantIds.ToArray() } : batch.MutantIds.Select(id => new[] { id });
      foreach (var ids in batchUnits)
      {
        scopes[UnitIdentity(ids)] = UnitScope(ids, batch, baseline.TestsById, workspace.Root, plan.PerTestIsolationIds);
      }
    }
    var checkpoint = await OpenAsync(options.CheckpointDirectory, fingerprint, units, cancellationToken, scopes, runtimeDependencies);
    var known = candidates.ToDictionary(candidate => candidate.Id);
    foreach (var pair in checkpoint.Restored.ToArray())
    {
      var candidate = known.GetValueOrDefault(pair.Key);
      if (candidate is null || pair.Value.Original != candidate.Original || pair.Value.Replacement != candidate.Replacement || pair.Value.SiteId != candidate.SiteId)
      {
        checkpoint.Restored.Remove(pair.Key);
      }
    }
    diagnostics.Add($"Checkpoint: validated fresh baseline; restored {checkpoint.Restored.Count} completed mutants from {Path.GetFullPath(options.CheckpointDirectory)}.");
    return checkpoint;
  }

  internal static async Task<RunCheckpoint> OpenAsync(string directory, string fingerprint, IReadOnlyList<string[]> units, CancellationToken cancellationToken,
      IReadOnlyDictionary<string, string>? unitScopes = null, IReadOnlyDictionary<string, string>? runtimeDependencies = null)
  {
    var cache = new ArtifactCache(directory);
    var byIdentity = units.ToDictionary(ids => UnitIdentity(ids), ids => ids.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    var scopes = byIdentity.Keys.ToDictionary(identity => identity, identity => unitScopes?[identity] ?? "", StringComparer.Ordinal);
    var checkpoint = new RunCheckpoint(cache, fingerprint, byIdentity, scopes);
    foreach (var (identity, ids) in byIdentity)
    {
      var read = await cache.TryReadAsync<CheckpointResults>("completed-units", identity, checkpoint.ContentKey(identity), cancellationToken);
      if (!read.Found || read.Value is null || !read.Value.Results.Select(result => result.Id).Order(StringComparer.Ordinal).SequenceEqual(ids) ||
          read.Value.Results.Any(result => result.Status is not (MutationStatus.Killed or MutationStatus.Survived)))
      {
        continue;
      }
      foreach (var result in read.Value.Results)
      {
        checkpoint.Restored[result.Id] = result;
      }
    }
    await cache.WriteAsync("run-manifest", "run", fingerprint, new
    {
      SchemaVersion = 2,
      Fingerprint = fingerprint,
      Units = byIdentity,
      UnitScopes = scopes,
      RuntimeDependencies = runtimeDependencies
    }, cancellationToken);
    return checkpoint;
  }

  internal async Task StoreAsync(IReadOnlyList<MutationResult> results, CancellationToken cancellationToken)
  {
    if (results.Any(result => result.Status is not (MutationStatus.Killed or MutationStatus.Survived)))
    {
      return;
    }
    var identity = UnitIdentity(results.Select(result => result.Id));
    if (!units.ContainsKey(identity))
    {
      throw new InvalidOperationException("Checkpoint results do not match a complete execution unit.");
    }
    await cache.WriteAsync("completed-units", identity, ContentKey(identity), new CheckpointResults(results.ToArray()), cancellationToken);
  }

  private string ContentKey(string identity) => ArtifactCache.ContentKey([], ["checkpoint-unit-v2", fingerprint, identity, unitScopes[identity]]);

  internal static string UnitScope(IEnumerable<string> ids, MutationBatch batch,
      IReadOnlyDictionary<string, TestCaseDescriptor> testsById, string workspaceRoot, IReadOnlySet<string>? perTestIsolationIds = null)
  {
    var membership = ids.ToHashSet(StringComparer.Ordinal);
    var tests = batch.TestToMutantId.Where(pair => membership.Contains(pair.Value)).Select(pair =>
    {
      var test = testsById[pair.Key];
      return pair.Value + "|" + test.Id + "|" + test.ExecutorUri + "|" + test.FullyQualifiedName + "|" +
          Path.GetRelativePath(workspaceRoot, test.SourceAssembly) + "|per-test=" + (perTestIsolationIds?.Contains(test.Id) == true);
    });
    return ArtifactCache.ContentKey([], tests.Order(StringComparer.Ordinal));
  }

  internal static string RuntimeDependencyHash(string file, string workspaceRoot)
  {
    // MVC's generated manifest contains project paths inside the random clone.
    // Only this known string dictionary is relocatable; user JSON remains byte hashed.
    var mvc = Path.GetFileName(file).Equals("MvcTestingAppManifest.json", StringComparison.OrdinalIgnoreCase);
    var staticAssets = Path.GetFileName(file).EndsWith(".staticwebassets.runtime.json", StringComparison.OrdinalIgnoreCase);
    if (!mvc && !staticAssets)
    {
      return CacheIdentity.HashFileContent(file);
    }
    using var document = JsonDocument.Parse(File.ReadAllText(file));
    if (document.RootElement.ValueKind != JsonValueKind.Object ||
        (mvc && document.RootElement.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String)) ||
        (staticAssets && (!document.RootElement.TryGetProperty("ContentRoots", out var contentRoots) || contentRoots.ValueKind != JsonValueKind.Array ||
            !document.RootElement.TryGetProperty("Root", out var assetRoot) || assetRoot.ValueKind != JsonValueKind.Object)))
    {
      return CacheIdentity.HashFileContent(file);
    }
    var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
      Write(document.RootElement, writer);
    }
    return CacheIdentity.Hash(System.Text.Encoding.UTF8.GetString(stream.ToArray()));

    void Write(JsonElement element, Utf8JsonWriter writer)
    {
      switch (element.ValueKind)
      {
        case JsonValueKind.Object:
          writer.WriteStartObject();
          foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
          {
            writer.WritePropertyName(property.Name);
            Write(property.Value, writer);
          }
          writer.WriteEndObject();
          break;
        case JsonValueKind.Array:
          writer.WriteStartArray();
          foreach (var item in element.EnumerateArray())
          {
            Write(item, writer);
          }
          writer.WriteEndArray();
          break;
        case JsonValueKind.String:
          var value = element.GetString()!;
          writer.WriteStringValue(Path.IsPathFullyQualified(value) && Path.GetFullPath(value).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
              ? "/_/mutagen/" + Path.GetRelativePath(root, value).Replace('\\', '/') : value);
          break;
        default:
          element.WriteTo(writer);
          break;
      }
    }
  }

  private static string UnitIdentity(IEnumerable<string> ids) => ArtifactCache.ContentKey([], ids.Order(StringComparer.Ordinal));
}
