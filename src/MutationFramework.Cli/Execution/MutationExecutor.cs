using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed record ExecutedMutations(MutationResult[] Results, bool Canceled, long[] WorkerBusyMs,
    long ExecutionMs, long WorkerInitializationMs, bool Executed);
internal static class MutationExecutor
{
  internal static async Task<ExecutedMutations> ExecuteAsync(List<Candidate> candidates,
      BaselineCoverage baseline, PlannedMutations plan, ValidatedMutationResults resultCache,
      MutationExecutionContext context, bool executeMutants, CancellationToken ct)
  {
    var opt = context.Options;
    var metrics = context.Metrics;
    var diagnostics = context.Diagnostics;
    var testsById = baseline.TestsById;
    var workItems = plan.WorkItems;
    var executionPlan = plan.ExecutionPlan;
    var perTestIsolationIds = plan.PerTestIsolationIds;
    var compileRejected = executionPlan.PreclassifiedMutants.Where(result => result.Status == MutationStatus.CompileError).ToArray();
    var timer = Stopwatch.StartNew();
    long executionMs = 0, workerInitializationMs = 0;
    var completed = new MutationResult[candidates.Count + compileRejected.Length];
    var resultIndexById = candidates.Select(candidate => candidate.Id).Concat(compileRejected.Select(result => result.Id))
        .Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);
    for (var rejectedIndex = 0; rejectedIndex < compileRejected.Length; rejectedIndex++)
    {
      completed[candidates.Count + rejectedIndex] = compileRejected[rejectedIndex];
    }

    foreach (var candidate in candidates)
    {
      if (resultCache.Restored.TryGetValue(candidate.Id, out var cachedResult))
      {
        completed[resultIndexById[candidate.Id]] = cachedResult;
      }
    }

    var schedule = ResourceAwareScheduler.Create(plan, baseline, opt, resultCache.Restored, executeMutants, diagnostics);
    var mutationWorkUnits = schedule.WorkUnits;
    var effectiveWorkerCount = schedule.WorkerCount;
    var runCanceled = false;
    timer.Restart();
    var workerBusy = new long[effectiveWorkerCount];
    if (!executeMutants)
    {
      foreach (var classifiedResult in executionPlan.PreclassifiedMutants)
      {
        completed[resultIndexById[classifiedResult.Id]] = classifiedResult;
      }

      foreach (var item in workItems)
      {
        var c = candidates[resultIndexById[item.MutantId]];
        completed[resultIndexById[item.MutantId]] = MutationResults.ToResult(c, MutationStatus.Pending,
            item.TestIds.Select(id => testsById[id].DisplayName).ToArray(), [], 0);
      }
    }
    else
    {
      foreach (var classifiedResult in executionPlan.PreclassifiedMutants)
      {
        completed[resultIndexById[classifiedResult.Id]] = classifiedResult;
      }

      var nextBatch = -1;
      var workers = Enumerable.Range(0, workerBusy.Length).Select(async workerIndex =>
      {
        var busy = Stopwatch.StartNew();
        using var worker = new MutationWorker(context.TestConsolePath, metrics);

        try
        {
          while (true)
          {
            var batchIndex = Interlocked.Increment(ref nextBatch);
            if (batchIndex >= mutationWorkUnits.Length) { busy.Stop(); workerBusy[workerIndex] = busy.ElapsedMilliseconds; return; }
            ct.ThrowIfCancellationRequested();
            var batch = mutationWorkUnits[batchIndex];
            if ((opt.TestHostIsolationPolicy is TestHostIsolationPolicy.PerBatch or TestHostIsolationPolicy.PerResourceGroup) &&
                executionPlan.SelectedMode == MutationExecutionMode.Batched)
            {
              var batchResults = await TestExecutor.ExecuteBatchAsync(batch,
                  mutantId => candidates[resultIndexById[mutantId]], testsById, worker.Session, context, ct);
              foreach (var result in batchResults)
              {
                completed[resultIndexById[result.Id]] = result;
              }
              var batchFailed = batchResults.Any(result => result.Status is MutationStatus.InfrastructureError or MutationStatus.Timeout);
              if (batchFailed)
              {
                worker.Restart();
              }
              continue;
            }

            foreach (var mutantId in batch.MutantIds)
            {
              ct.ThrowIfCancellationRequested();
              var i = resultIndexById[mutantId]; var c = candidates[i];
              if (completed[i] is not null)
              {
                continue;
              }
              var relevant = batch.TestToMutantId.Where(pair => pair.Value == mutantId).Select(pair => testsById[pair.Key]).OrderBy(test => test.Id, StringComparer.Ordinal).ToArray();
              var sharedHost = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerMutant &&
                  relevant.All(testCase => !perTestIsolationIds.Contains(testCase.Id));
              var mutationResult = await TestExecutor.ExecuteMutantAsync(c, relevant, worker.Session, context, sharedHost, ct);
              var status = mutationResult.Status;
              completed[i] = mutationResult;
              await resultCache.StoreAsync(c, mutationResult, ct);
              if (status is MutationStatus.InfrastructureError or MutationStatus.Timeout)
              {
                if (status == MutationStatus.InfrastructureError)
                {
                  Interlocked.Increment(ref metrics.WorkerFailures);
                }

                worker.Restart();
              }
            }
          }
        }
        finally { Interlocked.Add(ref workerInitializationMs, worker.InitializationMs); }
      }).ToArray();
      try { await Task.WhenAll(workers); }
      catch (OperationCanceledException)
      {
        runCanceled = true;
        for (var i = 0; i < candidates.Count; i++)
        {
          completed[i] ??= MutationResults.ToResult(candidates[i], MutationStatus.Inconclusive, [], [], 0, "Run canceled before this mutant completed.");
        }
      }
      catch (Exception ex)
      {
        Interlocked.Increment(ref metrics.WorkerFailures);
        diagnostics.Add("Mutation worker failed: " + ex.Message);
        for (var i = 0; i < candidates.Count; i++)
        {
          completed[i] ??= MutationResults.ToResult(candidates[i], MutationStatus.InfrastructureError, [], [], 0, "Worker failed before this mutant completed: " + ex.Message);
        }
      }
      executionMs = timer.ElapsedMilliseconds;
    }

    return new(completed, runCanceled, workerBusy, executionMs, workerInitializationMs, executeMutants);
  }
}
