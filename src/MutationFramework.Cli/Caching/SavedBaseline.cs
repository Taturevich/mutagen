using System.Security.Cryptography;
using System.Text.Json;

namespace MutationFramework.Sdk;

/// <summary>A complete Mutagen-native run plus the inputs required to make conservative delta decisions.</summary>
internal sealed record SavedBaseline(int SchemaVersion, string Engine, string ContextFingerprint,
    IReadOnlyDictionary<string, string> ProductionFiles, MutationRunResult Report, string Integrity);

internal static class SavedBaselineStore
{
  private const int SchemaVersion = 1;

  internal static async Task<IReadOnlyDictionary<string, MutationResult>> LoadReusableAsync(string path,
      string primaryProject, PreparedWorkspace workspace, ProjectEvaluation production, IReadOnlyList<ProjectEvaluation> tests, MutationOptions options,
      List<string> diagnostics, CancellationToken cancellationToken)
  {
    await using var baselineStream = File.OpenRead(path);
    using var document = await JsonDocument.ParseAsync(baselineStream, cancellationToken: cancellationToken);
    var saved = document.RootElement.Deserialize<SavedBaseline>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (saved is null || saved.SchemaVersion != SchemaVersion || saved.Report is null || saved.Report.ReportSchemaVersion != 4 ||
        saved.Report.OverallStatus != "Completed" || saved.Report.Mutants.Any(m => m.Status is not (MutationStatus.Killed or MutationStatus.Survived or MutationStatus.NoCoverage)))
    {
      throw new InvalidDataException("Saved baseline is incomplete or has an incompatible native report schema.");
    }
    var integrity = Integrity(saved with
    {
      Integrity = string.Empty
    });
    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(saved.Integrity), Convert.FromHexString(integrity)))
    {
      throw new InvalidDataException("Saved baseline integrity check failed.");
    }
    var context = Context(primaryProject, workspace, production, tests, options);
    if (!string.Equals(saved.Engine, CacheIdentity.EngineVersion(), StringComparison.Ordinal) ||
        !string.Equals(saved.ContextFingerprint, context, StringComparison.Ordinal))
    {
      diagnostics.Add("SavedBaseline: context changed; all covered mutants will be executed.");
      return new Dictionary<string, MutationResult>(StringComparer.Ordinal);
    }
    var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(primaryProject))!;
    var currentFiles = Snapshot(production.CompileFiles, sourceRoot);
    var changed = saved.ProductionFiles.Keys.Concat(currentFiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(file => !saved.ProductionFiles.TryGetValue(file, out var prior) || !currentFiles.TryGetValue(file, out var now) || prior != now)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (changed.Count != 0)
    {
      diagnostics.Add($"SavedBaseline: {changed.Count} production source file(s) changed; file-level execution coverage is unavailable, so all covered mutants will be executed.");
      return new Dictionary<string, MutationResult>(StringComparer.Ordinal);
    }
    var reusable = saved.Report.Mutants.Where(m => m.Status is MutationStatus.Killed or MutationStatus.Survived)
        .ToDictionary(m => m.Id, StringComparer.Ordinal);
    diagnostics.Add($"SavedBaseline: reused {reusable.Count} terminal results; no production source files changed.");
    return reusable;
  }

  internal static async Task SaveAsync(string path, string primaryProject, PreparedWorkspace workspace, MutationRunResult report, ProjectEvaluation production,
      IReadOnlyList<ProjectEvaluation> tests, MutationOptions options, CancellationToken cancellationToken)
  {
    if (report.OverallStatus != "Completed" || report.Mutants.Any(m => m.Status is not (MutationStatus.Killed or MutationStatus.Survived or MutationStatus.NoCoverage)))
    {
      throw new InvalidOperationException("Only a complete, error-free native Mutagen run can be saved as a baseline.");
    }
    var value = new SavedBaseline(SchemaVersion, CacheIdentity.EngineVersion(), Context(primaryProject, workspace, production, tests, options),
        Snapshot(production.CompileFiles, Path.GetDirectoryName(Path.GetFullPath(primaryProject))!), report, string.Empty);
    value = value with
    {
      Integrity = Integrity(value)
    };
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
      await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      File.Move(temp, full, true);
    }
    finally
    {
      if (File.Exists(temp))
      {
        File.Delete(temp);
      }
    }
  }

  internal static string Context(string primaryProject, PreparedWorkspace? workspace, ProjectEvaluation production,
      IReadOnlyList<ProjectEvaluation> tests, MutationOptions options)
  {
    var productionSources = production.CompileFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var inputs = production.BuildInputs.Concat(tests.SelectMany(e => e.BuildInputs)).Concat(tests.SelectMany(e => e.CompileFiles))
        .Concat(ProjectInputs(production.ProjectPaths.Concat(tests.SelectMany(e => e.ProjectPaths)), workspace))
        .Where(path => !productionSources.Contains(path))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    return ArtifactCache.ContentKey(inputs, ["saved-baseline-context-v1", CacheIdentity.EnvironmentFingerprint(),
      CacheIdentity.MutatorFingerprint(options), options.TargetFramework ?? "", options.TestFilter ?? "", options.Include ?? "", options.Exclude ?? "",
      Path.GetFullPath(primaryProject), options.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
      options.MaxRunTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
      options.HostStartupTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), options.PrioritizeFastTests.ToString(),
      options.TestHostIsolationPolicy.ToString(), options.ExecutionMode.ToString(),
      .. options.ResourceLocksByTestId.OrderBy(pair => pair.Key, StringComparer.Ordinal)
          .Select(pair => pair.Key + "=" + string.Join(",", pair.Value.Order(StringComparer.Ordinal))),
      options.BaselineScopeKey ?? ""]);
  }

  private static IEnumerable<string> ProjectInputs(IEnumerable<string> projects, PreparedWorkspace? workspace)
  {
    foreach (var project in projects.Distinct(StringComparer.OrdinalIgnoreCase))
    {
      var configuration = BuildInputs.ProjectConfigurationInputs(project, out var unresolved);
      if (unresolved is not null)
      {
        throw new InvalidOperationException($"Cannot use a saved baseline with unresolved build imports in '{project}': {unresolved}");
      }
      foreach (var input in configuration)
      {
        yield return input;
      }
      var assetsProject = workspace is null ? project : Path.Combine(workspace.Root, Path.GetRelativePath(workspace.SolutionRoot, project));
      var assets = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assetsProject))!, "obj", "project.assets.json");
      if (!File.Exists(assets))
      {
        throw new InvalidOperationException($"Cannot use a saved baseline without restored dependency assets for '{project}'.");
      }
      foreach (var asset in BuildInputs.PackageAssetInputs(assets))
      {
        yield return asset;
      }
    }
  }

  private static Dictionary<string, string> Snapshot(IEnumerable<string> paths, string root) => paths.Distinct(StringComparer.OrdinalIgnoreCase)
      .Where(File.Exists).ToDictionary(path => NormalizeSource(Path.GetRelativePath(root, path)), CacheIdentity.HashFileContent, StringComparer.OrdinalIgnoreCase);

  private static string NormalizeSource(string path) => path.Replace('\\', '/');

  private static string Integrity(SavedBaseline value)
  {
    var payload = JsonSerializer.Serialize(value with
    {
      Integrity = string.Empty
    });
    return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
  }
}
