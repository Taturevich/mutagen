using System.Diagnostics;

using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed record CachedTestDiscovery(TestIdentity[] Tests);

internal static class MutationDiscoverySession
{
  public static async Task<MutationDiscoveryResult> DiscoverAsync(MutationOptions opt, CancellationToken ct)
  {
    var (app, testProjects) = MutationOptionsValidator.ValidateForDiscovery(opt);
    var sw = Stopwatch.StartNew();
    var metrics = new RunMetrics();
    var diagnostics = new List<string>();
    var unsupported = new List<MutationResult>();
    var cache = CacheIdentity.CreateCache(opt, app);
    var evaluation = await CompilationWorkspace.GetProductionGraph(app, opt.ProductionProjects, metrics, ct, opt.TargetFramework, cache, diagnostics);
    var semanticCompilations = await ProjectSemanticCompilations.LoadAsync(app, evaluation, cache, diagnostics, ct, metrics);
    evaluation = ProjectSemanticCompilations.ApplyInputs(evaluation, semanticCompilations);
    var candidates = await MutationDiscovery.DiscoverCandidatesCached(evaluation.CompileFiles, app, opt, diagnostics, ct, unsupported, evaluation, cache, semanticCompilations, metrics);
    var testDlls = new List<string>();
    var testEvaluations = new List<ProjectEvaluation>(testProjects.Length);
    foreach (var test in testProjects)
    {
      var preRestoreEvaluation = await CompilationWorkspace.GetProjectGraph(test, metrics, ct, opt.TargetFramework, cache, diagnostics);
      using var compilationLocks = await CompilationArtifactCache.AcquireCompilationLocksAsync(preRestoreEvaluation.ProjectPaths, opt.TimeoutSeconds, ct);
      var restore = await ProcessRunner.RunProcess("dotnet", ["restore", test, "--nologo", "--verbosity", "quiet"],
          Path.GetDirectoryName(test)!, null, opt.TimeoutSeconds, metrics, ct);
      if (restore.ExitCode != 0)
      {
        throw new InvalidOperationException($"Test project restore failed ({test}). " + restore.Output);
      }

      var testEvaluation = await CompilationWorkspace.GetProjectGraph(test, metrics, ct, opt.TargetFramework, cache, diagnostics);
      testEvaluations.Add(testEvaluation);
      var framework = opt.TargetFramework ?? testEvaluation.TargetFramework;
      var targetPath = CompilationWorkspace.ResolveEvaluatedTargetPath(testEvaluation, test, framework);
      var compilationKey = CompilationArtifactCache.GetCompilationArtifactKey(test, testEvaluation, framework, out var canCacheCompilation, out var cacheBypassReason);
      var outputDirectory = Path.GetDirectoryName(targetPath)!;
      var logicalIdentity = Path.GetFullPath(test) + "|" + (framework ?? "default") + "|Debug";
      var reusedCompilation = false;
      var compilationRestore = canCacheCompilation
          ? await CompilationArtifactCache.TryRestoreCompilationArtifacts(cache, "compilation-artifacts", logicalIdentity, compilationKey, outputDirectory,
              privateOutputDirectory: false, cancellationToken: ct)
          : (Restored: false, Diagnostic: "");
      if (compilationRestore.Restored)
      {
        diagnostics.Add(compilationRestore.Diagnostic);
        reusedCompilation = true;
      }
      else
      {
        diagnostics.Add(canCacheCompilation ? compilationRestore.Diagnostic : $"CacheBypass: compilation artifacts for '{test}' are unsafe to reuse ({cacheBypassReason}).");
      }

      if (!reusedCompilation)
      {
        var buildArgs = new List<string> { "build", test, "-c", "Debug", "--no-restore", "--nologo", "-v:q" };
        if (!string.IsNullOrWhiteSpace(framework))
        {
          buildArgs.Add("-f");
          buildArgs.Add(framework);
        }
        var build = await ProcessRunner.RunProcess("dotnet", buildArgs, Path.GetDirectoryName(test)!, null, opt.TimeoutSeconds, metrics, ct);
        if (build.ExitCode != 0)
        {
          throw new InvalidOperationException($"Test project build failed ({test}). " + build.Output);
        }
      }

      testDlls.Add(CompilationWorkspace.FindTestAssembly(targetPath, test, testEvaluation.AssemblyName, framework));
    }
    var testConsole = TestPlatformPaths.FindConsole(testProjects[0]);
    var discoveryInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { testConsole };
    var discoveryCanCache = true;
    string? unresolvedTestImport = null;
    foreach (var testProject in testProjects)
    {
      discoveryInputs.UnionWith(BuildInputs.ProjectConfigurationInputs(testProject, out var unresolvedImport));
      unresolvedTestImport ??= unresolvedImport;
      discoveryCanCache &= unresolvedImport is null;
    }
    discoveryInputs.UnionWith(testEvaluations.SelectMany(evaluation => evaluation.CompileFiles));
    foreach (var testDll in testDlls)
    {
      var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(testDll))!;
      if (Directory.Exists(outputDirectory))
      {
        discoveryInputs.UnionWith(Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).Where(BuildInputs.IsStableTestDiscoveryInput));
      }
    }
    var discoveryKey = ArtifactCache.ContentKey(discoveryInputs,
        ["test-discovery-v1", string.Join("|", testProjects), opt.TargetFramework ?? string.Empty, opt.TestFilter ?? string.Empty,
          CacheIdentity.EnvironmentFingerprint(), CacheIdentity.EngineVersion(), typeof(TestPlatformSession).Assembly.ManifestModule.ModuleVersionId.ToString("N")]);
    var discoveryIdentity = string.Join("|", testProjects) + "|" + (opt.TargetFramework ?? "default") + "|" + (opt.TestFilter ?? "");
    TestIdentity[] testIdentities;
    var cacheDiagnostic = $"CacheBypass: test discovery depends on an MSBuild import with unresolved external inputs ({unresolvedTestImport}).";
    CachedTestDiscovery? cachedTests = null;
    var discoveryCacheHit = false;
    if (discoveryCanCache)
    {
      var cacheRead = await cache.TryReadAsync<CachedTestDiscovery>("test-discovery", discoveryIdentity, discoveryKey, ct);
      cachedTests = cacheRead.Value;
      cacheDiagnostic = cacheRead.Diagnostic;
      discoveryCacheHit = cacheRead.Found;
    }

    if (discoveryCacheHit)
    {
      diagnostics.Add(cacheDiagnostic);
      testIdentities = cachedTests!.Tests;
    }
    else
    {
      diagnostics.Add(cacheDiagnostic);
      using var platform = new TestPlatformSession(testConsole, metrics.StartedTestRunner);
      metrics.DiscoveryOperations++;
      var discoveredTests = platform.Discover(testDlls, ct);
      metrics.StartedTestHost(testDlls.Count);
      testIdentities = discoveredTests
          .Where(t => ExecutionPolicy.MatchesTestFilter(t, opt.TestFilter))
          .Select(t => new TestIdentity(t.Id, t.FullyQualifiedName, t.DisplayName, 0, t.ExecutorUri, t.SourceAssembly, t.Case.Id.ToString("N")))
          .OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
      if (discoveryCanCache)
      {
        await cache.WriteAsync("test-discovery", discoveryIdentity, discoveryKey, new CachedTestDiscovery(testIdentities), ct);
      }
    }
    return new MutationDiscoveryResult(candidates.Select(c => new MutationCandidateInfo(c.Id, c.SiteId, c.Operator, c.File, c.Line, c.Column, c.Original, c.Replacement)).ToArray(), testIdentities, diagnostics, sw.ElapsedMilliseconds, metrics.ProcessStarts)
    {
      UnsupportedMutations = unsupported
    };
  }
}
