using System.Numerics;

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
    int BatchCount, double AverageMutantsPerBatch, long EstimatedTestExecutions)
{
  /// <summary>Exact count, including plans exceeding the legacy Int32 representation.</summary>
  public long ExactConflictEdges { get; init; } = ConflictEdges;
  /// <summary>True when the legacy ConflictEdges field is capped at Int32.MaxValue.</summary>
  public bool ConflictEdgesOverflow => ExactConflictEdges > int.MaxValue;
  public bool ConflictEdgesComputed { get; init; } = true;
}

public sealed record MutationExecutionPlan(IReadOnlyList<MutationBatch> Batches,
    IReadOnlyList<MutationResult> PreclassifiedMutants, MutationPlanStatistics Statistics,
    MutationExecutionMode RequestedMode, MutationExecutionMode SelectedMode);

internal sealed record MutationWorkItem(string MutantId, IReadOnlyList<string> TestIds, long EstimatedDurationMs);

internal static class MutationPlanner
{
  public static MutationExecutionPlan Build(IReadOnlyList<MutationWorkItem> workItems,
      IReadOnlyList<MutationResult> preclassified, MutationExecutionMode requestedMode, bool coverageComplete, int timeoutSeconds, int maxWorkers,
      bool countConflicts = true)
  {
    var selectedMode = requestedMode switch
    {
      MutationExecutionMode.Isolated => MutationExecutionMode.Isolated,
      MutationExecutionMode.Batched when coverageComplete => MutationExecutionMode.Batched,
      MutationExecutionMode.Auto when coverageComplete => MutationExecutionMode.Isolated,
      _ => MutationExecutionMode.Isolated
    };
    if (requestedMode == MutationExecutionMode.Batched && !coverageComplete)
    {
      throw new InvalidOperationException("Batched execution requires complete, successful per-test coverage.");
    }

    var ordered = workItems.OrderByDescending(x => x.EstimatedDurationMs).ThenBy(x => x.MutantId, StringComparer.Ordinal).ToArray();
    var groups = new List<List<MutationWorkItem>>();
    // Store only actual test/batch memberships. The first-free lower bound makes a
    // single broad integration test linear rather than scanning all preceding batches.
    var occupied = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
    var firstFree = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var item in ordered)
    {
      var groupIndex = groups.Count;
      if (selectedMode == MutationExecutionMode.Batched)
      {
        groupIndex = 0;
        foreach (var test in item.TestIds)
        {
          groupIndex = Math.Max(groupIndex, firstFree.GetValueOrDefault(test));
        }
        while (groupIndex < groups.Count && item.TestIds.Any(test => occupied.TryGetValue(test, out var used) && used.Contains(groupIndex)))
        {
          groupIndex++;
        }
      }
      if (groupIndex == groups.Count)
      {
        groups.Add([]);
      }
      groups[groupIndex].Add(item);
      if (selectedMode != MutationExecutionMode.Batched)
      {
        continue;
      }
      foreach (var test in item.TestIds)
      {
        if (!occupied.TryGetValue(test, out var used))
        {
          occupied[test] = used = [];
        }
        used.Add(groupIndex);
        var next = firstFree.GetValueOrDefault(test);
        while (used.Contains(next))
        {
          next++;
        }
        firstFree[test] = next;
      }
    }

    if (selectedMode == MutationExecutionMode.Batched && workItems.Count > 1)
    {
      var targetBatchCount = Math.Min(workItems.Count, Math.Max(1, maxWorkers * 2));
      while (groups.Count < targetBatchCount)
      {
        var splitIndex = Enumerable.Range(0, groups.Count).Where(index => groups[index].Count > 1)
            .OrderByDescending(index => groups[index].Sum(item => item.EstimatedDurationMs))
            .ThenBy(index => groups[index].Min(item => item.MutantId), StringComparer.Ordinal).DefaultIfEmpty(-1).First();
        if (splitIndex < 0)
        {
          break;
        }

        var source = groups[splitIndex].OrderByDescending(item => item.EstimatedDurationMs).ThenBy(item => item.MutantId, StringComparer.Ordinal).ToArray();
        var partCount = Math.Min(source.Length, targetBatchCount - groups.Count + 1);
        var parts = Enumerable.Range(0, partCount).Select(_ => new List<MutationWorkItem>()).ToArray();
        var partDurations = new long[partCount];
        foreach (var item in source)
        {
          var part = Enumerable.Range(0, partCount).OrderBy(index => partDurations[index]).ThenBy(index => index).First();
          parts[part].Add(item);
          partDurations[part] += item.EstimatedDurationMs;
        }
        groups.RemoveAt(splitIndex);
        groups.InsertRange(splitIndex, parts.Where(part => part.Count > 0));
      }
    }

    var conflictEdges = countConflicts ? CountConflictEdges(workItems) : 0;

    var scheduledGroups = groups.Select(items =>
    {
      var testToMutant = new SortedDictionary<string, string>(StringComparer.Ordinal);
      foreach (var item in items)
      {
        foreach (var testId in item.TestIds)
        {
          if (!testToMutant.TryAdd(testId, item.MutantId))
          {
            throw new InvalidOperationException($"Planner assigned test '{testId}' to conflicting mutants.");
          }
        }
      }

      return (Items: items, Tests: testToMutant);
    }).OrderByDescending(group => group.Items.Sum(x => x.EstimatedDurationMs))
      .ThenBy(group => group.Items.Min(x => x.MutantId), StringComparer.Ordinal).ToArray();
    var batches = scheduledGroups.Select((group, index) => new MutationBatch(index,
        group.Items.Select(item => item.MutantId).ToArray(), group.Tests.Keys.ToArray(), group.Tests,
        group.Items.Sum(x => x.EstimatedDurationMs), true, timeoutSeconds)).ToArray();
    var covered = workItems.Count;
    return new MutationExecutionPlan(batches, preclassified,
        new MutationPlanStatistics(covered, preclassified.Count(x => x.Status == MutationStatus.NoCoverage), (int)Math.Min(int.MaxValue, conflictEdges),
            batches.Length, batches.Length == 0 ? 0 : (double)covered / batches.Length,
            workItems.Sum(x => (long)x.TestIds.Count))
        {
          ExactConflictEdges = conflictEdges,
          ConflictEdgesComputed = countConflicts
        }, requestedMode, selectedMode);
  }

  internal static long CountConflictEdges(IReadOnlyList<MutationWorkItem> items)
  {
    // Sparse blocks avoid N*T storage for disjoint suites; dense tests use N/64 words.
    var byTest = new Dictionary<string, Dictionary<int, ulong>>(StringComparer.Ordinal);
    var testNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
    var identicalCoverage = new Dictionary<string, (IReadOnlyList<string> Tests, int Count)>(StringComparer.Ordinal);
    for (var index = 0; index < items.Count; index++)
    {
      var tests = items[index].TestIds.Distinct(StringComparer.Ordinal).ToArray();
      foreach (var test in tests)
      {
        if (!byTest.TryGetValue(test, out var words))
        {
          byTest[test] = words = [];
          testNumbers[test] = testNumbers.Count;
        }
        var word = index >> 6;
        words[word] = words.GetValueOrDefault(word) | (1UL << (index & 63));
      }
      var key = string.Join(",", tests.Select(test => testNumbers[test]).Order());
      identicalCoverage.TryGetValue(key, out var group);
      identicalCoverage[key] = (tests, group.Count + 1);
    }
    var neighborhood = new ulong[(items.Count + 63) / 64];
    var touched = new List<int>();
    long directedEdges = 0;
    foreach (var (tests, count) in identicalCoverage.Values)
    {
      foreach (var test in tests)
      {
        foreach (var (word, bits) in byTest[test])
        {
          if (neighborhood[word] == 0)
          {
            touched.Add(word);
          }
          neighborhood[word] |= bits;
        }
      }
      long neighbors = 0;
      foreach (var word in touched)
      {
        neighbors += BitOperations.PopCount(neighborhood[word]);
        neighborhood[word] = 0;
      }
      touched.Clear();
      if (tests.Count > 0)
      {
        directedEdges += (neighbors - 1) * count;
      }
    }
    return directedEdges / 2;
  }
}
