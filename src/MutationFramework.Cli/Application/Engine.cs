using System.Diagnostics;

namespace MutationFramework.Sdk;

internal static class Engine
{
  public static async Task<MutationRunResult> RunAsync(MutationOptions opt, bool executeMutants, CancellationToken ct)
  {
    var (app, tests) = MutationOptionsValidator.ValidateForRun(opt);
    var output = Path.GetFullPath(opt.OutputDirectory);
    Directory.CreateDirectory(output);
    var sw = Stopwatch.StartNew();
    var diagnostics = new List<string>();
    var unsupportedMutations = new List<MutationResult>();
    var metrics = new RunMetrics();
    var session = Path.Combine(Path.GetTempPath(), "mutation-framework", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(session);
    try
    {
      var timer = Stopwatch.StartNew();
      var cache = CacheIdentity.CreateCache(opt, app);
      var evaluation = await CompilationWorkspace.GetProductionGraph(app, opt.ProductionProjects, metrics, ct, opt.TargetFramework, cache, diagnostics);
      var testEvaluations = new List<ProjectEvaluation>(tests.Length);
      foreach (var test in tests)
      {
        testEvaluations.Add(await CompilationWorkspace.GetProjectGraph(test, metrics, ct, opt.TargetFramework, cache, diagnostics));
      }
      var semanticCompilations = await ProjectSemanticCompilations.LoadAsync(app, evaluation, cache, diagnostics, ct, metrics);
      evaluation = ProjectSemanticCompilations.ApplyInputs(evaluation, semanticCompilations);
      var projects = new MutationProjects(app, tests, evaluation, testEvaluations);
      var sourceFiles = evaluation.CompileFiles;
      var evaluationMs = timer.ElapsedMilliseconds;
      var candidates = await MutationDiscovery.DiscoverCandidatesCached(sourceFiles, app, opt, diagnostics, ct, unsupportedMutations, evaluation, cache, semanticCompilations, metrics);
      var generationMs = timer.ElapsedMilliseconds - evaluationMs;
      var workspace = MutationWorkspace.Prepare(projects, candidates, session, diagnostics);
      var compilation = await new MutationCompiler(opt, cache, diagnostics, metrics).CompileAsync(projects, workspace, candidates, ct);
      using var resourceLockManager = new ResourceLockManager();
      var baseline = await new CoverageCollector(opt, cache, diagnostics, metrics).CollectAsync(projects, workspace, compilation, candidates, resourceLockManager, ct);
      timer.Restart();
      var plan = MutationPlanning.Create(candidates, compilation, baseline, opt, diagnostics);
      var planningMs = timer.ElapsedMilliseconds;
      var resultCache = await MutationResultCache.PrepareAsync(projects, compilation, candidates, baseline, plan,
          opt, executeMutants, cache, diagnostics, ct);
      var executionContext = new MutationExecutionContext(opt, session, TestPlatformPaths.FindConsole(workspace.TestProjects[0]),
          resourceLockManager, metrics, diagnostics);
      var execution = await MutationExecutor.ExecuteAsync(candidates, baseline, plan, resultCache, executionContext, executeMutants, ct);
      var timings = new PhaseTimings(baseline.DiscoveryMs, compilation.BuildMs, baseline.BaselineMs, baseline.CoverageMs,
          generationMs, execution.ExecutionMs, 0)
      {
        SemanticAnalysisMs = metrics.SemanticAnalysisMs,
        ProbeSerializationMs = baseline.ProbeSerializationMs,
        CoverageMappingMs = baseline.CoverageMappingMs,
        ProjectEvaluationMs = evaluationMs,
        ProjectCopyMs = workspace.CopyMs,
        InstrumentationMs = workspace.InstrumentationMs + compilation.RecoveryInstrumentationMs,
        MutationPlanningMs = Math.Max(0, planningMs),
        WorkerInitializationMs = execution.WorkerInitializationMs
      };
      var result = MutationResultAggregator.Create(opt, baseline, plan, execution,
          new(timings, metrics, sw.ElapsedMilliseconds), unsupportedMutations, diagnostics);
      result = await MutationReportWriter.WriteAsync(result, opt.Report, sw, ct);
      Console.WriteLine($"Mutants: {result.Mutants.Count}; killed: {result.KilledCount}; survived: {result.SurvivedCount}; no coverage: {result.NoCoverageCount}; score: {(result.MutationScore is null ? "n/a" : result.MutationScore.Value.ToString("F1") + "%")}");
      return result;
    }
    finally
    {
      // A terminated host may briefly retain a file handle; cleanup failure must not hide run results.
      try
      {
        Directory.Delete(session, true);
      }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
  }
}
