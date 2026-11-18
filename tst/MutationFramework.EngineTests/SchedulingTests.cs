using Microsoft.CodeAnalysis;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

using static MutationFramework.EngineTests.TestWorkspace;

[TestFixture]
public sealed class SchedulingTests
{
  [Test]
  [Category("Component")]
  public void ThousandTestCoverageAndASparseHubRemainExactAndConflictSafe()
  {
    var broadTests = Enumerable.Range(0, 1000).Select(index => $"T{index}").ToArray();
    var broad = Enumerable.Range(0, 512).Select(index => new MutationWorkItem($"M{index:D6}", broadTests, 1)).ToArray();
    var broadPlan = MutationPlanner.Build(broad, [], MutationExecutionMode.Batched, true, 30, 4);
    Assert.That(broadPlan.Statistics.ExactConflictEdges, Is.EqualTo(512L * 511 / 2));
    Assert.That(broadPlan.Batches, Has.Count.EqualTo(512));
    Assert.That(broadPlan.Batches.All(batch => batch.MutantIds.Count == 1), Is.True);

    var hub = Enumerable.Range(0, 1000).Select(index => new MutationWorkItem($"M{index:D6}",
        index < 100 ? [$"T{index}", "hub"] : [$"T{index}"], 1)).ToArray();
    var hubPlan = MutationPlanner.Build(hub, [], MutationExecutionMode.Batched, true, 30, 4);
    Assert.That(hubPlan.Statistics.ExactConflictEdges, Is.EqualTo(4950));
    Assert.That(hubPlan.Batches.SelectMany(batch => batch.MutantIds).Distinct().Count(), Is.EqualTo(hub.Length));
    foreach (var batch in hubPlan.Batches)
    {
      var tests = batch.MutantIds.SelectMany(id => hub[int.Parse(id.AsSpan(1))].TestIds).ToArray();
      Assert.That(tests.Distinct().Count(), Is.EqualTo(tests.Length));
    }
  }

  [Test]
  [Category("Component")]
  public void HundredThousandDenseMutantsRetainAnExactWideConflictCount()
  {
    var items = Enumerable.Range(0, 100000).Select(index => new MutationWorkItem($"M{index:D6}", ["integration-test"], 1)).ToArray();
    var plan = MutationPlanner.Build(items, [], MutationExecutionMode.Batched, true, 30, 4);
    Assert.That(plan.Statistics.ExactConflictEdges, Is.EqualTo(4_999_950_000L));
    Assert.That(plan.Statistics.ConflictEdges, Is.EqualTo(int.MaxValue));
    Assert.That(plan.Statistics.ConflictEdgesOverflow, Is.True);
    Assert.That(plan.Batches.Count, Is.EqualTo(100000));
  }

  [Test]
  [Category("Component")]
  public void DenseTenThousandMutantPlanIsExactDeterministicAndBounded()
  {
    var items = Enumerable.Range(0, 10000).Select(index => new MutationWorkItem($"M{index:D6}", ["integration-test"], 1)).ToArray();
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var first = MutationPlanner.Build(items, [], MutationExecutionMode.Batched, true, 30, 4);
    var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    Assert.That(first.Statistics.ExactConflictEdges, Is.EqualTo(49995000));
    Assert.That(first.Statistics.ConflictEdges, Is.EqualTo(49995000));
    Assert.That(first.Batches, Has.Count.EqualTo(10000));
    Assert.That(first.Batches.All(batch => batch.MutantIds.Count == 1), Is.True);
    Assert.That(first.Batches.SelectMany(batch => batch.MutantIds).Distinct().Count(), Is.EqualTo(10000));
    Assert.That(bytes, Is.LessThan(100_000_000), "Planner allocation must not grow with pairwise edge count.");
    Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
    var second = MutationPlanner.Build(items.Reverse().ToArray(), [], MutationExecutionMode.Batched, true, 30, 4);
    Assert.That(second.Batches.Select(batch => string.Join(",", batch.MutantIds)),
        Is.EqualTo(first.Batches.Select(batch => string.Join(",", batch.MutantIds))));
  }

  [Test]
  [Category("Unit")]
  public void CompactConflictCountsMatchPairwiseOracleForRandomOverlaps()
  {
    var random = new Random(1729);
    for (var iteration = 0; iteration < 20; iteration++)
    {
      var items = Enumerable.Range(0, 100).Select(index => new MutationWorkItem($"M{index}",
          Enumerable.Range(0, 40).Where(_ => random.Next(10) == 0).Select(test => $"T{test}").ToArray(), 1)).ToArray();
      long expected = 0;
      for (var left = 0; left < items.Length; left++)
      {
        for (var right = left + 1; right < items.Length; right++)
        {
          if (items[left].TestIds.Intersect(items[right].TestIds).Any())
          {
            expected++;
          }
        }
      }
      Assert.That(MutationPlanner.CountConflictEdges(items), Is.EqualTo(expected));
      var plan = MutationPlanner.Build(items, [], MutationExecutionMode.Batched, true, 30, 4);
      foreach (var batch in plan.Batches)
      {
        var tests = batch.MutantIds.SelectMany(id => items.Single(item => item.MutantId == id).TestIds).ToArray();
        Assert.That(tests.Distinct().Count(), Is.EqualTo(tests.Length));
      }
    }
  }

  [Test]
  [Category("Unit")]
  public void ResourceLockConfigurationMustNameDiscoveredTestsAndResources()
  {
    var configured = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = ["shared-database"]
    };
    Assert.DoesNotThrow(() => MutationOptionsValidator.ValidateResourceLockIdentities(configured, ["test-a", "test-b"]));
    var unknown = Assert.Throws<ArgumentException>(() => MutationOptionsValidator.ValidateResourceLockIdentities(configured, ["test-b"]));
    Assert.That(unknown!.Message, Does.Contain("test-a"));

    var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = []
    };
    Assert.Throws<ArgumentException>(() => MutationOptionsValidator.ValidateResourceLockIdentities(empty, ["test-a"]));

    var blankName = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = [" "]
    };
    Assert.Throws<ArgumentException>(() => MutationOptionsValidator.ValidateResourceLockIdentities(blankName, ["test-a"]));

    var nullList = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = null!
    };
    Assert.Throws<ArgumentException>(() => MutationOptionsValidator.ValidateResourceLockIdentities(nullList, ["test-a"]));
  }

  [Test]
  [Category("Unit")]
  public async Task DeclaredResourceLocksSerializeConcurrentTestsWithoutDeadlock()
  {
    using var locks = new ResourceLockManager();
    var active = 0;
    var peak = 0;
    async Task<bool> Run(IReadOnlyList<string> resourceKeys)
    {
      return await locks.ExecuteAsync(resourceKeys, async () =>
      {
        var now = Interlocked.Increment(ref active);
        int observed;
        do
        {
          observed = Volatile.Read(ref peak);
          if (observed >= now)
          {
            break;
          }
        }
        while (Interlocked.CompareExchange(ref peak, now, observed) != observed);
        await Task.Delay(75);
        Interlocked.Decrement(ref active);
        return true;
      }, CancellationToken.None);
    }

    var both = await Task.WhenAll(Run(["database", "shared-cache"]), Run(["shared-cache", "database"]));

    Assert.That(both, Is.All.True);
    Assert.That(peak, Is.EqualTo(1));
  }

  [Test]
  [Category("Unit")]
  public void BatchedPlannerNeverAssignsOverlappingTestsToDifferentMutants()
  {
    var items = new[]
    {
            new MutationWorkItem("M1", ["T1", "T2"], 20),
            new MutationWorkItem("M2", ["T2", "T3"], 18),
            new MutationWorkItem("M3", ["T4"], 10),
            new MutationWorkItem("M4", ["T5"], 5),
            new MutationWorkItem("M5", ["T1", "T5"], 4)
        };

    var plan = MutationPlanner.Build(items, [], MutationExecutionMode.Batched, coverageComplete: true, timeoutSeconds: 30, maxWorkers: 2);

    Assert.That(plan.Statistics.ConflictEdges, Is.EqualTo(3));
    Assert.That(plan.Batches.SelectMany(batch => batch.MutantIds).Order(StringComparer.Ordinal),
        Is.EqualTo(items.Select(item => item.MutantId).Order(StringComparer.Ordinal)));
    foreach (var batch in plan.Batches)
    {
      Assert.That(batch.TestToMutantId.Values.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(batch.MutantIds.Count));
      Assert.That(batch.TestToMutantId.Values.All(batch.MutantIds.Contains), Is.True);
    }
    Assert.That(plan.Batches.All(batch => batch.RequiredIsolation), Is.True);
    Assert.That(plan.Batches.Count, Is.LessThan(items.Length));
    Assert.That(plan.Batches.Any(batch => batch.MutantIds.Count > 1), Is.True);
  }

  [Test]
  [Category("Unit")]
  public void PerBatchWorkerCountIsCappedWhenEveryBatchContendsForResourceLocks()
  {
    var batches = new[]
    {
      new MutationBatch(0, ["M1"], ["T1", "T2"], new Dictionary<string, string> { ["T1"] = "M1", ["T2"] = "M1" }, 10, true, 30),
      new MutationBatch(1, ["M2"], ["T3", "T4"], new Dictionary<string, string> { ["T3"] = "M2", ["T4"] = "M2" }, 10, true, 30),
      new MutationBatch(2, ["M3"], ["T5", "T6"], new Dictionary<string, string> { ["T5"] = "M3", ["T6"] = "M3" }, 10, true, 30)
    };
    var locks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["T1"] = ["database-a"],
      ["T2"] = ["database-b"],
      ["T3"] = ["database-a"],
      ["T4"] = ["database-b"],
      ["T5"] = ["database-a"],
      ["T6"] = ["database-b"]
    };

    Assert.That(ResourceAwareScheduler.GetEffectiveWorkerCount(4, batches, TestHostIsolationPolicy.PerBatch, locks), Is.EqualTo(1));
    Assert.That(ResourceAwareScheduler.GetEffectiveWorkerCount(4, batches, TestHostIsolationPolicy.PerTest, locks), Is.EqualTo(3));
    Assert.That(ResourceAwareScheduler.GetEffectiveWorkerCount(4, batches.Take(1).ToArray(), TestHostIsolationPolicy.PerBatch, locks), Is.EqualTo(1));
    Assert.That(ResourceAwareScheduler.GetEffectiveWorkerCount(4, [], TestHostIsolationPolicy.PerBatch, locks), Is.Zero);

    var independentLocks = new Dictionary<string, IReadOnlyList<string>>(locks, StringComparer.Ordinal)
    {
      ["T3"] = ["database-c"],
      ["T4"] = ["database-d"],
      ["T5"] = ["database-e"],
      ["T6"] = ["database-f"]
    };
    Assert.That(ResourceAwareScheduler.GetEffectiveWorkerCount(4, batches, TestHostIsolationPolicy.PerBatch, independentLocks), Is.EqualTo(3));
  }

  [Test]
  [Category("Unit")]
  public void ResourceGroupPlannerKeepsOverlappingLocksTogetherAndPartitionsEachMutantOnce()
  {
    var locks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = ["database-a"],
      ["test-b"] = ["database-b"],
      ["test-c"] = ["database-a", "queue-c"],
      ["test-d"] = ["queue-c"]
    };
    var groups = ResourceGroupPlanner.CreateGroups(locks.Keys, locks);
    Assert.That(groups["test-a"].Id, Is.EqualTo(groups["test-c"].Id));
    Assert.That(groups["test-c"].Id, Is.EqualTo(groups["test-d"].Id));
    Assert.That(groups["test-a"].Id, Is.Not.EqualTo(groups["test-b"].Id));

    var testToMutant = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
      ["test-a"] = "mutant-cross-group",
      ["test-b"] = "mutant-local",
      ["test-c"] = "mutant-cross-group",
      ["test-d"] = "mutant-cross-group"
    };
    var batch = new MutationBatch(3, ["mutant-cross-group", "mutant-local"], testToMutant.Keys.ToArray(),
        testToMutant, 100, true, 120);
    var partitions = ResourceGroupPlanner.SplitBatch(batch, groups);
    Assert.That(partitions.SelectMany(partition => partition.MutantIds).Order(StringComparer.Ordinal),
        Is.EqualTo(batch.MutantIds.Order(StringComparer.Ordinal)));
    Assert.That(partitions.SelectMany(partition => partition.TestIds).Order(StringComparer.Ordinal),
        Is.EqualTo(batch.TestIds.Order(StringComparer.Ordinal)));
    Assert.That(partitions, Has.Length.EqualTo(2));
    Assert.That(partitions.Single(partition => partition.MutantIds.Contains("mutant-cross-group")).TestIds,
        Is.EquivalentTo(new[] { "test-a", "test-c", "test-d" }));
  }

  [Test]
  [Category("EndToEnd")]
  public async Task ResourceGroupPolicyKeepsCrossGroupMutantsInOneLockedExecutionUnit()
  {
    const string testFilter = "AddAsync_RunsWithFixtureAndStaticInitialization";
    var discoveryOptions = Options(2, mode: MutationExecutionMode.Batched, isolationPolicy: TestHostIsolationPolicy.PerBatch,
        testFilter: testFilter);
    var discovery = await MutationEngine.DiscoverAsync(discoveryOptions);
    Assert.That(discovery.Tests, Has.Length.EqualTo(2));
    var locks = discovery.Tests.OrderBy(test => test.DisplayName, StringComparer.Ordinal)
        .Select((test, index) => (test.Id, Resource: (IReadOnlyList<string>)[$"independent-db-{index}"]))
        .ToDictionary(pair => pair.Id, pair => pair.Resource, StringComparer.Ordinal);

    var batchedOutput = NewOutputDirectory();
    var groupedOutput = NewOutputDirectory();
    try
    {
      var batched = await MutationEngine.RunAsync(Options(2, batchedOutput, MutationExecutionMode.Batched,
          TestHostIsolationPolicy.PerBatch, testFilter, locks));
      var grouped = await MutationEngine.RunAsync(Options(2, groupedOutput, MutationExecutionMode.Batched,
          TestHostIsolationPolicy.PerResourceGroup, testFilter, locks));

      Assert.That(grouped.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, grouped.Diagnostics));
      Assert.That(grouped.Mutants.Select(mutant => $"{mutant.Id}|{mutant.Status}").Order(StringComparer.Ordinal),
          Is.EqualTo(batched.Mutants.Select(mutant => $"{mutant.Id}|{mutant.Status}").Order(StringComparer.Ordinal)));
      Assert.That(grouped.Counters.MutationTestExecutions, Is.EqualTo(grouped.ExecutionPlan!.Statistics.EstimatedTestExecutions));
      var profilesByName = grouped.Tests.ToDictionary(test => test.DisplayName, test =>
          grouped.ExecutionProfiles.Single(profile => profile.TestId == test.Id).ResourceIsolationGroup, StringComparer.Ordinal);
      Assert.That(profilesByName.Values.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(2));
      var crossGroupMutants = grouped.Mutants.Where(mutant => mutant.CoveringTests
          .Select(testName => profilesByName[testName]).Distinct(StringComparer.Ordinal).Skip(1).Any()).ToArray();
      Assert.That(crossGroupMutants, Is.Not.Empty, "The fixture must exercise a mutant covered by tests in separate resource groups.");
      Assert.That(grouped.Diagnostics, Has.Some.Contains("ResourceGroupScheduling"));
    }
    finally
    {
      if (Directory.Exists(batchedOutput))
      {
        Directory.Delete(batchedOutput, true);
      }
      if (Directory.Exists(groupedOutput))
      {
        Directory.Delete(groupedOutput, true);
      }
    }
  }

  [Test]
  [Category("Unit")]
  public void AutoPlannerFallsBackToIsolatedWhenCoverageIsIncomplete()
  {
    var items = new[] { new MutationWorkItem("M1", ["T1"], 1), new MutationWorkItem("M2", ["T2"], 1) };

    var plan = MutationPlanner.Build(items, [], MutationExecutionMode.Auto, coverageComplete: false, timeoutSeconds: 30, maxWorkers: 2);

    Assert.That(plan.SelectedMode, Is.EqualTo(MutationExecutionMode.Isolated));
    Assert.That(plan.Batches, Has.Count.EqualTo(2));
  }

  [Test]
  [Category("EndToEnd")]
  public async Task PerMutantHostPolicySerializesTestsWithDeclaredResourceLocksInOneHost()
  {
    var discoveryOptions = Options(2, NewOutputDirectory(), MutationExecutionMode.Isolated, TestHostIsolationPolicy.PerMutant,
        testFilter: "Add_ReturnsCorrectSum");
    var discovery = await MutationEngine.DiscoverAsync(discoveryOptions);
    var selectedTest = discovery.Tests.Single(test => test.DisplayName.Contains("Add_ReturnsCorrectSum", StringComparison.Ordinal));
    var resourceLocks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      [selectedTest.Id] = ["shared-database"]
    };
    var output = NewOutputDirectory();
    try
    {
      var result = await MutationEngine.RunAsync(Options(2, output, MutationExecutionMode.Isolated,
          TestHostIsolationPolicy.PerMutant, "Add_ReturnsCorrectSum", resourceLocks));

      var executionProfile = result.ExecutionProfiles.Single(profile => profile.TestId == selectedTest.Id);
      Assert.That(executionProfile.IsolationRequired, Is.False);
      Assert.That(executionProfile.TestHostIsolationPolicy, Is.EqualTo(TestHostIsolationPolicy.PerMutant));
      Assert.That(executionProfile.TimeoutPolicy, Is.EqualTo(TestRunTimeoutPolicy.AggregatePerMutant));
      Assert.That(executionProfile.TestsSerializedInSharedHost, Is.True);
      Assert.That(executionProfile.ResourceLocks, Does.Contain("shared-database"));
      Assert.That(result.Diagnostics, Has.None.Contains("TestHostIsolationFallback"));
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }
}
