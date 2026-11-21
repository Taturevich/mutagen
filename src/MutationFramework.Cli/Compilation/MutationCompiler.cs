using System.Diagnostics;

using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed record CompiledMutations(List<string> TestDlls, List<MutationResult> Rejected, long BuildMs, long RecoveryInstrumentationMs);
internal sealed class MutationCompiler(MutationOptions options, ArtifactCache cache, List<string> diagnostics, RunMetrics metrics)
{
  internal async Task<CompiledMutations> CompileAsync(MutationProjects projects, PreparedWorkspace workspace,
      List<Candidate> candidates, CancellationToken ct)
  {
    var opt = options;
    var app = projects.App;
    var tests = projects.Tests;
    var evaluation = projects.Production;
    var testEvaluations = projects.TestEvaluations;
    var clonedTests = workspace.TestProjects;
    var mappedFiles = workspace.Sources;
    var session = workspace.Session;
    var appCopy = workspace.AppDirectory;
    var workspaceCopy = workspace.Root;
    var solutionRoot = workspace.SolutionRoot;
    var sourceMappings = MutationWorkspace.CreateSourceMappings(projects, workspace);
    var compilationSourceSnapshot = BuildInputs.SnapshotCompilationSources(workspaceCopy);
    var compileRejected = new List<MutationResult>();
    var timer = Stopwatch.StartNew();
    var testDlls = new List<string>();
    // The isolated workspace intentionally excludes obj/bin. Restore the cloned test graph
    // before its --no-restore builds so project.assets.json and generated NuGet imports are
    // rooted at the clone. Restoring the original project does not make those paths valid.
    foreach (var clonedTest in clonedTests)
    {
      var restore = await ProcessRunner.RunProcess("dotnet", ["restore", clonedTest, "--nologo", "--verbosity", "quiet", "--ignore-failed-sources"],
          Path.GetDirectoryName(clonedTest)!, null, opt.TimeoutSeconds, metrics, ct);
      if (restore.ExitCode != 0)
      {
        throw new InvalidOperationException($"Mutation workspace restore failed ({clonedTest}). " + restore.Output);
      }
    }

    long recoveryInstrumentationMs = 0;
    var recoveryPasses = 0;
    var recoveryLimit = Math.Clamp(evaluation.SourceOwnership.Count * 2, 2, 16);
    while (true)
    {
      testDlls.Clear();
      var retryBuild = false;
      for (var testIndex = 0; testIndex < clonedTests.Count; testIndex++)
      {
        var clonedTest = clonedTests[testIndex];
        var testEvaluation = testEvaluations[testIndex];
        var framework = opt.TargetFramework ?? testEvaluation.TargetFramework;
        var buildArgs = new List<string> { "build", clonedTest, "-c", "Debug", "--no-restore", "--nologo", "-v:q" };
        buildArgs.Add($"-p:PathMap={workspaceCopy}=/_/mutagen");
        if (!string.IsNullOrWhiteSpace(framework))
        {
          buildArgs.Add("-f");
          buildArgs.Add(framework);
        }
        var originalTargetPath = CompilationWorkspace.ResolveEvaluatedTargetPath(testEvaluation, tests[testIndex], framework);
        var relativeTargetPath = Path.GetRelativePath(Path.GetDirectoryName(tests[testIndex])!, originalTargetPath);
        var clonedTargetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(clonedTest)!, relativeTargetPath));
        var outputDirectory = Path.GetDirectoryName(clonedTargetPath)!;
        var compilationKey = CompilationArtifactCache.GetMutationCompilationArtifactKey(tests[testIndex], testEvaluation, framework, candidates, opt,
            out var canCacheCompilation, out var cacheBypassReason);
        var logicalIdentity = Path.GetFullPath(tests[testIndex]) + "|" + (framework ?? "default") + "|Debug|mutation-schema";
        var compilationRestore = recoveryPasses == 0 && canCacheCompilation
            ? await CompilationArtifactCache.TryRestoreCompilationArtifacts(cache, "mutation-compilation-artifacts", logicalIdentity, compilationKey, outputDirectory,
                privateOutputDirectory: true, cancellationToken: ct)
            : (Restored: false, Diagnostic: "");
        if (compilationRestore.Restored)
        {
          diagnostics.Add(compilationRestore.Diagnostic);
        }
        else if (recoveryPasses != 0 || !canCacheCompilation)
        {
          diagnostics.Add($"CacheBypass: mutation compilation artifacts for '{tests[testIndex]}' are unsafe to reuse ({cacheBypassReason ?? "candidate recovery changed the schema"}).");
        }
        else
        {
          diagnostics.Add(compilationRestore.Diagnostic);
        }

        var build = compilationRestore.Restored
            ? new ProcessRunner.Proc(0, string.Empty)
            : await ProcessRunner.RunProcess("dotnet", buildArgs, session, null, opt.TimeoutSeconds, metrics, ct);
        if (build.ExitCode != 0)
        {
          var invalidCandidates = MutationSchemaBuilder.FindInvalidCandidates(build.Output, candidates, appCopy, sourceMappings);
          if (invalidCandidates.Count == 0 || recoveryPasses >= recoveryLimit)
          {
            throw new InvalidOperationException("Mutation-enabled project build failed without a safely attributable candidate, or the bounded recovery limit was reached. " + build.Output);
          }

          foreach (var failure in invalidCandidates)
          {
            compileRejected.Add(MutationResults.ToResult(failure.Candidate, MutationStatus.CompileError, [], [], 0, failure.Diagnostic));
            diagnostics.Add($"CompileError: {failure.Candidate.Id} at {failure.Candidate.File}:{failure.Candidate.Line}: {failure.Diagnostic}");
          }
          var rejectedIds = invalidCandidates.Select(failure => failure.Candidate.Id).ToHashSet(StringComparer.Ordinal);
          candidates.RemoveAll(candidate => rejectedIds.Contains(candidate.Id));
          MutationDiscovery.ReindexCandidates(candidates);
          var recoveryTimer = Stopwatch.StartNew();
          foreach (var pair in mappedFiles)
          {
            File.Copy(pair.Key, pair.Value, true);
          }

          MutationWorkspace.RewriteWorkspaceSources(mappedFiles, candidates, app, workspaceCopy, solutionRoot, evaluation);
          MutationRuntimeSource.WriteRuntimeSources(workspaceCopy, solutionRoot, evaluation.SourceOwnership.Select(owner => owner.Project).ToArray(), candidates);
          recoveryTimer.Stop();
          recoveryInstrumentationMs += recoveryTimer.ElapsedMilliseconds;
          recoveryPasses++;
          Console.Error.WriteLine($"Mutagen: compilation recovery {recoveryPasses}/{recoveryLimit}; rejected {invalidCandidates.Count} mapped candidates; {candidates.Count} remain.");
          retryBuild = true;
          break;
        }

        testDlls.Add(CompilationWorkspace.FindTestAssembly(testEvaluation.TargetPath, tests[testIndex], testEvaluation.AssemblyName, framework, clonedTest));
      }
      if (!retryBuild)
      {
        break;
      }
    }
    var buildMs = timer.ElapsedMilliseconds - recoveryInstrumentationMs;
    var generatedCompileChange = BuildInputs.FindUnsafeGeneratedCompileChange(workspaceCopy, compilationSourceSnapshot);
    if (generatedCompileChange is not null)
    {
      diagnostics.Add($"CacheBypass: mutation compilation artifacts were not stored because the build generated or changed an untracked C# input '{generatedCompileChange}'.");
    }

    if (recoveryPasses == 0 && generatedCompileChange is null)
    {
      for (var testIndex = 0; testIndex < testDlls.Count; testIndex++)
      {
        var testEvaluation = testEvaluations[testIndex];
        var framework = opt.TargetFramework ?? testEvaluation.TargetFramework;
        var compilationKey = CompilationArtifactCache.GetMutationCompilationArtifactKey(tests[testIndex], testEvaluation, framework, candidates, opt,
            out var canCacheCompilation, out _);
        if (canCacheCompilation)
        {
          var logicalIdentity = Path.GetFullPath(tests[testIndex]) + "|" + (framework ?? "default") + "|Debug|mutation-schema";
          await CompilationArtifactCache.StoreCompilationArtifacts(cache, "mutation-compilation-artifacts", logicalIdentity, compilationKey,
              Path.GetDirectoryName(testDlls[testIndex])!, ct);
        }
      }
    }

    return new(testDlls, compileRejected, buildMs, recoveryInstrumentationMs);
  }
}
