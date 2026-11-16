namespace MutationFramework.Sdk;

internal sealed record ResourceGroup(string Id, IReadOnlyList<string> TestIds, IReadOnlyList<string> ResourceLocks);

internal static class ResourceGroupPlanner
{
  public static IReadOnlyDictionary<string, ResourceGroup> CreateGroups(
      IEnumerable<string> testIds, IReadOnlyDictionary<string, IReadOnlyList<string>> resourceLocksByTestId)
  {
    var tests = testIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    if (tests.Length == 0)
    {
      return new Dictionary<string, ResourceGroup>(StringComparer.Ordinal);
    }

    var parent = Enumerable.Range(0, tests.Length).ToArray();
    int Find(int index)
    {
      while (parent[index] != index)
      {
        parent[index] = parent[parent[index]];
        index = parent[index];
      }
      return index;
    }

    void Union(int left, int right)
    {
      var leftRoot = Find(left);
      var rightRoot = Find(right);
      if (leftRoot != rightRoot)
      {
        parent[Math.Max(leftRoot, rightRoot)] = Math.Min(leftRoot, rightRoot);
      }
    }

    var firstTestByLock = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var testIndex = 0; testIndex < tests.Length; testIndex++)
    {
      var locks = resourceLocksByTestId.GetValueOrDefault(tests[testIndex], []);
      if (locks.Count == 0)
      {
        throw new ArgumentException($"Resource-group isolation requires at least one declared resource lock for test '{tests[testIndex]}'.",
            nameof(resourceLocksByTestId));
      }

      foreach (var resourceLock in locks.Distinct(StringComparer.Ordinal))
      {
        if (firstTestByLock.TryGetValue(resourceLock, out var firstTestIndex))
        {
          Union(testIndex, firstTestIndex);
        }
        else
        {
          firstTestByLock.Add(resourceLock, testIndex);
        }
      }
    }

    var groups = Enumerable.Range(0, tests.Length)
        .GroupBy(Find)
        .Select(group => group.Select(index => tests[index]).Order(StringComparer.Ordinal).ToArray())
        .OrderBy(group => group[0], StringComparer.Ordinal)
        .Select((group, index) => new ResourceGroup(
            $"resource-group-{index:D4}", group,
            group.SelectMany(testId => resourceLocksByTestId[testId]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
        .ToArray();

    return groups.SelectMany(group => group.TestIds.Select(testId => (testId, group)))
        .ToDictionary(pair => pair.testId, pair => pair.group, StringComparer.Ordinal);
  }

  public static IReadOnlyList<MutationBatch> SplitBatch(MutationBatch batch,
      IReadOnlyDictionary<string, ResourceGroup> groupsByTestId)
  {
    var testIdsByMutant = batch.TestToMutantId
        .GroupBy(pair => pair.Value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    var crossGroupMutants = testIdsByMutant
        .Where(pair => pair.Value.Select(testId => groupsByTestId[testId].Id).Distinct(StringComparer.Ordinal).Skip(1).Any())
        .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

    var partitions = new List<(string Id, string[] Mutants, string[] Tests)>();
    foreach (var group in batch.TestIds.Select(testId => groupsByTestId[testId]).DistinctBy(group => group.Id)
        .OrderBy(group => group.Id, StringComparer.Ordinal))
    {
      var mutantIds = batch.MutantIds.Where(mutantId => !crossGroupMutants.Contains(mutantId) &&
          testIdsByMutant[mutantId].All(testId => groupsByTestId[testId].Id == group.Id)).ToArray();
      if (mutantIds.Length != 0)
      {
        var testIds = mutantIds.SelectMany(mutantId => testIdsByMutant[mutantId]).Order(StringComparer.Ordinal).ToArray();
        partitions.Add((group.Id, mutantIds, testIds));
      }
    }

    if (crossGroupMutants.Count != 0)
    {
      var mutantIds = batch.MutantIds.Where(crossGroupMutants.Contains).ToArray();
      var testIds = mutantIds.SelectMany(mutantId => testIdsByMutant[mutantId]).Distinct(StringComparer.Ordinal)
          .Order(StringComparer.Ordinal).ToArray();
      partitions.Add(("resource-group-cross", mutantIds, testIds));
    }

    var scheduled = partitions.Select(partition =>
    {
      var testToMutant = new SortedDictionary<string, string>(StringComparer.Ordinal);
      foreach (var mutantId in partition.Mutants)
      {
        foreach (var testId in testIdsByMutant[mutantId])
        {
          if (!testToMutant.TryAdd(testId, mutantId))
          {
            throw new InvalidOperationException($"Resource-group scheduler duplicated test '{testId}' within a planned batch.");
          }
        }
      }
      return new MutationBatch(batch.RunId, partition.Mutants, testToMutant.Keys.ToArray(), testToMutant,
          batch.EstimatedDurationMs, batch.RequiredIsolation, batch.TimeoutSeconds);
    }).ToArray();

    if (scheduled.SelectMany(partition => partition.MutantIds).Distinct(StringComparer.Ordinal).Count() != batch.MutantIds.Count ||
        scheduled.SelectMany(partition => partition.TestIds).Distinct(StringComparer.Ordinal).Count() != batch.TestIds.Count)
    {
      throw new InvalidOperationException($"Resource-group scheduler did not partition planned batch {batch.RunId} exactly once.");
    }

    return scheduled;
  }
}
