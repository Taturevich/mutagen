namespace MutationFramework.Sdk;

internal sealed record MutationSchedule(MutationBatch[] WorkUnits, int WorkerCount);

internal static class ResourceAwareScheduler
{
  internal static MutationSchedule Create(PlannedMutations plan, BaselineCoverage baseline, MutationOptions opt,
      IReadOnlyDictionary<string, MutationResult> restoredResults, bool executeMutants, List<string> diagnostics)
  {
    var executionPlan = plan.ExecutionPlan;
    var resourceGroupsByTestId = baseline.ResourceGroupsByTestId;
    var batchesToExecute = executionPlan.Batches.Where(batch => batch.MutantIds.Any(mutantId =>
        !restoredResults.ContainsKey(mutantId))).ToArray();
    var splitWorkUnits = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup
        ? batchesToExecute.SelectMany(batch => ResourceGroupPlanner.SplitBatch(batch, resourceGroupsByTestId)).ToArray()
        : batchesToExecute;
    var mutationWorkUnits = splitWorkUnits.Where(batch => batch.MutantIds.Any(mutantId => !restoredResults.ContainsKey(mutantId))).ToArray();
    var activeResourceGroupCount = opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup
        ? mutationWorkUnits.SelectMany(batch => batch.TestIds).Select(testId => resourceGroupsByTestId[testId].Id)
            .Distinct(StringComparer.Ordinal).Count()
        : 0;
    var effectiveWorkerCount = executeMutants
        ? opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup
            ? Math.Min(Math.Min(opt.MaxWorkers, activeResourceGroupCount), mutationWorkUnits.Length)
            : GetEffectiveWorkerCount(opt.MaxWorkers, mutationWorkUnits, opt.TestHostIsolationPolicy, opt.ResourceLocksByTestId)
        : 0;
    if (opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup)
    {
      diagnostics.Add($"ResourceGroupScheduling: split {batchesToExecute.Length} coverage batches into {mutationWorkUnits.Length} process-isolated resource-group work units across {activeResourceGroupCount} covered groups, bounded by {effectiveWorkerCount} workers.");
    }
    if (opt.TestHostIsolationPolicy == TestHostIsolationPolicy.PerBatch && effectiveWorkerCount < Math.Min(opt.MaxWorkers, mutationWorkUnits.Length))
    {
      diagnostics.Add($"ResourceConstrainedConcurrency: reduced mutation workers from {Math.Min(opt.MaxWorkers, batchesToExecute.Length)} to {effectiveWorkerCount} because every per-batch execution shares a declared resource lock.");
    }

    return new(mutationWorkUnits, effectiveWorkerCount);
  }

  internal static int GetEffectiveWorkerCount(int maxWorkers, IReadOnlyList<MutationBatch> batches,
      TestHostIsolationPolicy isolationPolicy, IReadOnlyDictionary<string, IReadOnlyList<string>> resourceLocksByTestId)
  {
    if (batches.Count == 0)
    {
      return 0;
    }

    var workerCount = Math.Min(maxWorkers, batches.Count);
    if (workerCount <= 1 || isolationPolicy != TestHostIsolationPolicy.PerBatch)
    {
      return workerCount;
    }

    var batchLocks = batches.Select(batch => batch.TestIds
        .SelectMany(testId => resourceLocksByTestId.GetValueOrDefault(testId, []))
        .ToHashSet(StringComparer.Ordinal)).ToArray();
    var commonLocks = new HashSet<string>(batchLocks[0], StringComparer.Ordinal);
    foreach (var locks in batchLocks.Skip(1))
    {
      commonLocks.IntersectWith(locks);
    }
    if (commonLocks.Count != 0)
    {
      return 1;
    }
    var items = batchLocks.Select((locks, index) => new MutationWorkItem(index.ToString(), locks.ToArray(), 0)).ToArray();
    return MutationPlanner.CountConflictEdges(items) == (long)batchLocks.Length * (batchLocks.Length - 1) / 2 ? 1 : workerCount;
  }
}
