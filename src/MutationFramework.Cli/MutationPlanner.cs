namespace MutationFramework.Sdk;

public enum MutationExecutionMode
{
    Auto,
    Isolated,
    Batched
}

public sealed record MutationBatch(int RunId, IReadOnlyList<string> MutantIds, IReadOnlyList<string> TestIds,
    IReadOnlyDictionary<string, string> TestToMutantId, long EstimatedDurationMs, bool RequiredIsolation, int TimeoutSeconds);

public sealed record MutationPlanStatistics(int CoveredMutants, int UncoveredMutants, int ConflictEdges,
    int BatchCount, double AverageMutantsPerBatch, long EstimatedTestExecutions);

public sealed record MutationExecutionPlan(IReadOnlyList<MutationBatch> Batches,
    IReadOnlyList<MutationResult> PreclassifiedMutants, MutationPlanStatistics Statistics,
    MutationExecutionMode RequestedMode, MutationExecutionMode SelectedMode);

internal sealed record MutationWorkItem(string MutantId, IReadOnlyList<string> TestIds, long EstimatedDurationMs);

internal static class MutationPlanner
{
    public static MutationExecutionPlan Build(IReadOnlyList<MutationWorkItem> workItems,
        IReadOnlyList<MutationResult> preclassified, MutationExecutionMode requestedMode, bool coverageComplete, int timeoutSeconds, int maxWorkers)
    {
        var selectedMode = requestedMode switch
        {
            MutationExecutionMode.Isolated => MutationExecutionMode.Isolated,
            MutationExecutionMode.Batched when coverageComplete => MutationExecutionMode.Batched,
            MutationExecutionMode.Auto when coverageComplete => MutationExecutionMode.Isolated,
            _ => MutationExecutionMode.Isolated
        };
        if (requestedMode == MutationExecutionMode.Batched && !coverageComplete)
            throw new InvalidOperationException("Batched execution requires complete, successful per-test coverage.");

        var testIndexes = workItems.SelectMany(x => x.TestIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select((testId, index) => (testId, index)).ToDictionary(x => x.testId, x => x.index, StringComparer.Ordinal);
        int wordCount = (testIndexes.Count + 63) / 64;
        ulong[] CoverageMask(MutationWorkItem item)
        {
            var mask = new ulong[wordCount];
            foreach (var testId in item.TestIds)
            {
                int bit = testIndexes[testId];
                mask[bit >> 6] |= 1UL << (bit & 63);
            }
            return mask;
        }
        var ordered = workItems.OrderByDescending(x => x.EstimatedDurationMs).ThenBy(x => x.MutantId, StringComparer.Ordinal)
            .Select(item => (Item: item, Mask: CoverageMask(item))).ToArray();
        var groups = new List<List<MutationWorkItem>>();
        var masksByGroup = new List<ulong[]>();
        foreach (var (item, mask) in ordered)
        {
            int groupIndex = selectedMode == MutationExecutionMode.Isolated ? -1 :
                masksByGroup.FindIndex(existing => !Intersects(existing, mask));
            if (groupIndex < 0)
            {
                groupIndex = groups.Count;
                groups.Add([]);
                masksByGroup.Add(new ulong[wordCount]);
            }
            groups[groupIndex].Add(item);
            for (int word = 0; word < wordCount; word++) masksByGroup[groupIndex][word] |= mask[word];
        }

        if (selectedMode == MutationExecutionMode.Batched && workItems.Count > 1)
        {
            int targetBatchCount = Math.Min(workItems.Count, Math.Max(1, maxWorkers * 2));
            while (groups.Count < targetBatchCount)
            {
                int splitIndex = Enumerable.Range(0, groups.Count).Where(index => groups[index].Count > 1)
                    .OrderByDescending(index => groups[index].Sum(item => item.EstimatedDurationMs))
                    .ThenBy(index => groups[index].Min(item => item.MutantId), StringComparer.Ordinal).DefaultIfEmpty(-1).First();
                if (splitIndex < 0) break;
                var source = groups[splitIndex].OrderByDescending(item => item.EstimatedDurationMs).ThenBy(item => item.MutantId, StringComparer.Ordinal).ToArray();
                int partCount = Math.Min(source.Length, targetBatchCount - groups.Count + 1);
                var parts = Enumerable.Range(0, partCount).Select(_ => new List<MutationWorkItem>()).ToArray();
                var partDurations = new long[partCount];
                foreach (var item in source)
                {
                    int part = Enumerable.Range(0, partCount).OrderBy(index => partDurations[index]).ThenBy(index => index).First();
                    parts[part].Add(item);
                    partDurations[part] += item.EstimatedDurationMs;
                }
                groups.RemoveAt(splitIndex);
                groups.InsertRange(splitIndex, parts.Where(part => part.Count > 0));
            }
        }

        var mutantsByTest = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int index = 0; index < workItems.Count; index++)
            foreach (var testId in workItems[index].TestIds)
            {
                if (!mutantsByTest.TryGetValue(testId, out var indexes)) mutantsByTest[testId] = indexes = [];
                indexes.Add(index);
            }
        var conflictPairs = new HashSet<long>();
        foreach (var indexes in mutantsByTest.Values)
            for (int left = 0; left < indexes.Count; left++)
                for (int right = left + 1; right < indexes.Count; right++)
                {
                    int a = Math.Min(indexes[left], indexes[right]), b = Math.Max(indexes[left], indexes[right]);
                    conflictPairs.Add(((long)a << 32) | (uint)b);
                }

        var scheduledGroups = groups.Select(items =>
        {
            var testToMutant = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in items)
                foreach (var testId in item.TestIds)
                    if (!testToMutant.TryAdd(testId, item.MutantId))
                        throw new InvalidOperationException($"Planner assigned test '{testId}' to conflicting mutants.");
            return (Items: items, Tests: testToMutant);
        }).OrderByDescending(group => group.Items.Sum(x => x.EstimatedDurationMs))
          .ThenBy(group => group.Items.Min(x => x.MutantId), StringComparer.Ordinal).ToArray();
        var batches = scheduledGroups.Select((group, index) => new MutationBatch(index,
            group.Items.Select(item => item.MutantId).ToArray(), group.Tests.Keys.ToArray(), group.Tests,
            group.Items.Sum(x => x.EstimatedDurationMs), true, timeoutSeconds)).ToArray();
        int covered = workItems.Count;
        return new MutationExecutionPlan(batches, preclassified,
            new MutationPlanStatistics(covered, preclassified.Count(x => x.Status == MutationStatus.NoCoverage), conflictPairs.Count,
                batches.Length, batches.Length == 0 ? 0 : (double)covered / batches.Length,
                workItems.Sum(x => (long)x.TestIds.Count)), requestedMode, selectedMode);
    }

    private static bool Intersects(ulong[] left, ulong[] right)
    {
        for (int i = 0; i < left.Length; i++) if ((left[i] & right[i]) != 0) return true;
        return false;
    }
}
