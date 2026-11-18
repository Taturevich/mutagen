using System.Text.Json;

using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed record CachedMutationExecutionResult(MutationResult Result, string IntegrityHash);

internal static class MutationResultCache
{
  internal static string? CreateMutationResultCacheFingerprint(string app, ProjectEvaluation productionEvaluation,
      IReadOnlyList<ProjectEvaluation> testEvaluations, IReadOnlyList<string> testDlls, IReadOnlyList<TestCaseDescriptor> testCases,
      IReadOnlyDictionary<string, IReadOnlyList<string>> testToSites, IReadOnlyList<Candidate> candidates, MutationOptions options,
      out string? bypassReason, out string fingerprintComponents)
  {
    bypassReason = null;
    fingerprintComponents = string.Empty;
    var projectPaths = productionEvaluation.ProjectPaths.Concat(testEvaluations.SelectMany(evaluation => evaluation.ProjectPaths))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var inputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var project in projectPaths)
    {
      var configurationInputs = BuildInputs.ProjectConfigurationInputs(project, out var unresolvedImport);
      if (unresolvedImport is not null)
      {
        bypassReason = $"project '{project}' has unresolved imports ({unresolvedImport})";
        return null;
      }

      inputPaths.UnionWith(configurationInputs);
      var assetsFile = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
      if (!File.Exists(assetsFile))
      {
        bypassReason = $"project '{project}' has no restored dependency assets";
        return null;
      }
      inputPaths.Add(assetsFile);
      try
      {
        inputPaths.UnionWith(BuildInputs.PackageAssetInputs(assetsFile));
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
      {
        bypassReason = $"project '{project}' dependency assets cannot be fingerprinted ({exception.Message})";
        return null;
      }
    }
    inputPaths.UnionWith(productionEvaluation.CompileFiles);
    inputPaths.UnionWith(productionEvaluation.BuildInputs);
    foreach (var testEvaluation in testEvaluations)
    {
      inputPaths.UnionWith(testEvaluation.CompileFiles);
      inputPaths.UnionWith(testEvaluation.BuildInputs);
    }

    var outputFingerprints = new List<string>();
    foreach (var testDll in testDlls.Order(StringComparer.OrdinalIgnoreCase))
    {
      var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(testDll))!;
      try
      {
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                     .Where(BuildInputs.IsStableTestDiscoveryInput)
                     .Where(CacheIdentity.IsRuntimeDependencyFile)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
          outputFingerprints.Add(Path.GetFileName(testDll) + "|" + Path.GetRelativePath(outputDirectory, file).Replace('\\', '/') + "|" + CacheIdentity.HashFileContent(file));
        }
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        bypassReason = $"test output for '{testDll}' cannot be fingerprinted ({exception.Message})";
        return null;
      }
    }

    var mutatorFingerprint = CacheIdentity.MutatorFingerprint(options);
    var values = new List<string>
    {
      "validated-mutation-results-v1", Path.GetFullPath(app), options.TargetFramework ?? string.Empty,
      options.MutationResultCacheScopeKey!, options.TestFilter ?? string.Empty, options.Include ?? string.Empty,
      options.Exclude ?? string.Empty, options.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
      options.TestHostIsolationPolicy.ToString(), CacheIdentity.EnvironmentFingerprint(), CacheIdentity.EngineVersion(),
      CacheIdentity.HostVersion(), mutatorFingerprint,
      MutationRuntimeSource.RuntimeSource(candidates.Select(candidate => candidate.SiteRuntimeId).Distinct(StringComparer.Ordinal).Count()),
      string.Join(";", projectPaths.Order(StringComparer.OrdinalIgnoreCase)),
      string.Join(";", testCases.OrderBy(testCase => testCase.Id, StringComparer.Ordinal)
          .Select(testCase => string.Join("|", testCase.Id, testCase.FullyQualifiedName, testCase.DisplayName,
              testCase.ExecutorUri, testCase.SourceAssembly))),
      string.Join(";", testToSites.OrderBy(pair => pair.Key, StringComparer.Ordinal)
          .Select(pair => pair.Key + "=" + string.Join(",", pair.Value.Order(StringComparer.Ordinal)))),
      string.Join(";", productionEvaluation.ParseOptionsByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
          .Select(pair => pair.Key + "=" + pair.Value.LanguageVersion + ":" + string.Join(",", pair.Value.PreprocessorSymbolNames.Order(StringComparer.Ordinal))))
    };
    values.AddRange(outputFingerprints.Order(StringComparer.Ordinal));
    string inputFingerprint;
    try
    {
      inputFingerprint = ArtifactCache.ContentKey(inputPaths, []);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      bypassReason = $"project input cannot be fingerprinted ({exception.Message})";
      return null;
    }
    var valueFingerprint = ArtifactCache.ContentKey([], values);
    var outputFingerprint = ArtifactCache.ContentKey([], outputFingerprints.Order(StringComparer.Ordinal));
    fingerprintComponents = $"inputs={inputFingerprint[..12]} values={valueFingerprint[..12]} outputs={outputFingerprint[..12]}";
    return ArtifactCache.ContentKey([], ["validated-mutation-result-fingerprint-v1", inputFingerprint, valueFingerprint, outputFingerprint]);
  }

  internal static string CreateMutationResultCacheKey(string fingerprint, Candidate candidate, IReadOnlyList<TestCaseDescriptor> tests) =>
      ArtifactCache.ContentKey([], ["validated-mutation-result-v1", fingerprint, candidate.Id, candidate.SiteId,
          candidate.Operator, candidate.File, candidate.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
          candidate.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), candidate.Original, candidate.Replacement,
          candidate.ReplacementToken, candidate.SiteRuntimeId, candidate.MutantRuntimeId,
          string.Join(";", tests.Select(test => string.Join("|", test.Id, test.FullyQualifiedName, test.DisplayName,
              test.ExecutorUri, test.SourceAssembly)))]);

  internal static bool IsValidCachedMutationResult(CachedMutationExecutionResult cached, Candidate candidate,
      IReadOnlyList<TestCaseDescriptor> relevantTests)
  {
    var result = cached.Result;
    var coveredTestNames = relevantTests.Select(test => test.DisplayName).ToArray();
    return (result.Status is MutationStatus.Killed or MutationStatus.Survived) &&
        string.Equals(result.Id, candidate.Id, StringComparison.Ordinal) &&
        string.Equals(result.SiteId, candidate.SiteId, StringComparison.Ordinal) &&
        string.Equals(result.Operator, candidate.Operator, StringComparison.Ordinal) &&
        string.Equals(result.SourceFile, candidate.File, StringComparison.Ordinal) &&
        result.Line == candidate.Line && result.Column == candidate.Column &&
        string.Equals(result.Original, candidate.Original, StringComparison.Ordinal) &&
        string.Equals(result.Replacement, candidate.Replacement, StringComparison.Ordinal) &&
        result.CoveringTests.SequenceEqual(coveredTestNames, StringComparer.Ordinal) &&
        result.KillingTests.All(coveredTestNames.Contains) &&
        (result.Status == MutationStatus.Killed) == (result.KillingTests.Count > 0) &&
        string.Equals(cached.IntegrityHash, MutationResultIntegrity(result), StringComparison.Ordinal);
  }

  internal static string MutationResultIntegrity(MutationResult result) => ArtifactCache.ContentKey([], [
      "validated-mutation-result-record-v1", result.Id, result.SiteId, result.Operator, result.SourceFile,
      result.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Column.ToString(System.Globalization.CultureInfo.InvariantCulture),
      result.Original, result.Replacement, result.Status.ToString(),
      string.Join(";", result.CoveringTests), string.Join(";", result.KillingTests),
      result.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Diagnostic ?? string.Empty
    ]);
  internal static async Task<ValidatedMutationResults> PrepareAsync(MutationProjects projects,
      CompiledMutations compilation, List<Candidate> candidates, BaselineCoverage baseline, PlannedMutations plan,
      MutationOptions opt, bool executeMutants, ArtifactCache cache, List<string> diagnostics, CancellationToken ct)
  {
    var app = projects.App;
    var evaluation = projects.Production;
    var testEvaluations = projects.TestEvaluations;
    var testDlls = compilation.TestDlls;
    var testCases = baseline.TestCases;
    var testsById = baseline.TestsById;
    var testToSites = baseline.TestToSites;
    var executionPlan = plan.ExecutionPlan;
    var coverageComplete = plan.CoverageComplete;
    var workItems = plan.WorkItems;
    var candidatesById = candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
    var restored = new Dictionary<string, MutationResult>(StringComparer.Ordinal);
    var mutationResultCacheKeys = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!string.IsNullOrWhiteSpace(opt.MutationResultCacheScopeKey))
    {
      if (!executeMutants || executionPlan.SelectedMode != MutationExecutionMode.Isolated ||
          opt.TestHostIsolationPolicy != TestHostIsolationPolicy.PerTest || opt.ResourceLocksByTestId.Count != 0 || !coverageComplete)
      {
        diagnostics.Add("CacheBypass: validated mutation results require RunAsync, isolated mutation execution, per-test host isolation, no declared resource locks, and complete baseline coverage.");
      }
      else
      {
        var resultCacheFingerprint = MutationResultCache.CreateMutationResultCacheFingerprint(app, evaluation, testEvaluations, testDlls,
            testCases, testToSites, candidates, opt, out var cacheBypassReason, out var fingerprintComponents);
        if (resultCacheFingerprint is null)
        {
          diagnostics.Add($"CacheBypass: validated mutation results are unsafe to reuse ({cacheBypassReason}).");
        }
        else
        {
          diagnostics.Add($"CacheFingerprint: validated-mutation-results {fingerprintComponents}");
          foreach (var item in workItems)
          {
            var candidate = candidatesById[item.MutantId];
            var relevantTests = item.TestIds.Select(testId => testsById[testId]).OrderBy(testCase => testCase.Id, StringComparer.Ordinal).ToArray();
            var resultKey = MutationResultCache.CreateMutationResultCacheKey(resultCacheFingerprint, candidate, relevantTests);
            mutationResultCacheKeys[item.MutantId] = resultKey;
            var logicalIdentity = Path.GetFullPath(app) + "|" + (opt.TargetFramework ?? "default") + "|" + candidate.Id;
            var cacheRead = await cache.TryReadAsync<CachedMutationExecutionResult>("validated-mutation-results",
                logicalIdentity, resultKey, ct);
            if (cacheRead.Found &&
                cacheRead.Value is { } cached &&
                cached?.Result is { } cachedResult && MutationResultCache.IsValidCachedMutationResult(cached, candidate, relevantTests))
            {
              restored[item.MutantId] = cachedResult with
              {
                Diagnostic = "Reused a previously validated mutation result from the content-addressed cache."
              };
              diagnostics.Add(cacheRead.Diagnostic);
            }
            else if (cacheRead.Found)
            {
              diagnostics.Add($"CacheInvalidated: validated-mutation-results {resultKey} (cached result failed identity or integrity validation)");
            }
            else
            {
              diagnostics.Add(cacheRead.Diagnostic);
            }
          }
        }
      }
    }

    return new(cache, Path.GetFullPath(app) + "|" + (opt.TargetFramework ?? "default") + "|", mutationResultCacheKeys, restored);
  }
}

internal sealed class ValidatedMutationResults(ArtifactCache cache, string identityPrefix,
    IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, MutationResult> restored)
{
  internal IReadOnlyDictionary<string, MutationResult> Restored { get; } = restored;

  internal async Task StoreAsync(Candidate candidate, MutationResult result, CancellationToken cancellationToken)
  {
    if (result.Status is (MutationStatus.Killed or MutationStatus.Survived) && keys.TryGetValue(candidate.Id, out var key))
    {
      await cache.WriteAsync("validated-mutation-results", identityPrefix + candidate.Id, key,
          new CachedMutationExecutionResult(result, MutationResultCache.MutationResultIntegrity(result)), cancellationToken);
    }
  }
}
