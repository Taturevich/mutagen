using MutationFramework.Sdk;
using NUnit.Framework;
using Microsoft.CodeAnalysis;

namespace MutationFramework.EngineTests;

using static MutationFramework.EngineTests.TestWorkspace;

[TestFixture]
public sealed class ExecutionTests
{
  [Test]
  [Category("Unit")]
  public void TestProfilesDistinguishPerTestPerMutantAndMixedExecutionPolicies()
  {
    Assert.That(ExecutionPolicy.ResolveTestExecutionPolicies(true, false),
        Is.EqualTo((TestHostIsolationPolicy.PerTest, TestRunTimeoutPolicy.PerTest)));
    Assert.That(ExecutionPolicy.ResolveTestExecutionPolicies(false, true),
        Is.EqualTo((TestHostIsolationPolicy.PerMutant, TestRunTimeoutPolicy.AggregatePerMutant)));
    Assert.That(ExecutionPolicy.ResolveTestExecutionPolicies(false, false),
        Is.EqualTo((TestHostIsolationPolicy.PerMutant, TestRunTimeoutPolicy.AggregatePerMutant)));
    Assert.That(ExecutionPolicy.ResolveTestExecutionPolicies(true, true),
        Is.EqualTo((TestHostIsolationPolicy.Mixed, TestRunTimeoutPolicy.Mixed)));
  }

  [Test]
  [Category("Unit")]
  public void MutationOptionsDefaultToOneFreshTestHostPerMutant()
  {
    var options = new MutationOptions { ProjectPath = "production.csproj", TestProjects = ["tests.csproj"] };
    Assert.That(options.TestHostIsolationPolicy, Is.EqualTo(TestHostIsolationPolicy.PerMutant));
  }

  [Test]
  [Category("EndToEnd")]
  public async Task PlanKeepsThrowingExpressionCoveredWithoutExecutingMutants()
  {
    var output = NewOutputDirectory();
    try
    {
      var result = await MutationEngine.PlanAsync(Options(1, output));
      var throwingTest = result.Tests.Single(t => t.DisplayName == "ThrowingOperand_StillEntersCoverageSite");
      var throwingMutation = result.Mutants.First(m => m.Original.Contains("value() > 10", StringComparison.Ordinal));

      Assert.That(result.OverallStatus, Is.EqualTo("Planned"));
      Assert.That(result.ReportSchemaVersion, Is.EqualTo(4));
      Assert.That(result.UnsupportedMutations, Is.Not.Empty);
      Assert.That(result.UnsupportedMutations.All(mutation => mutation.Status == MutationStatus.Unsupported), Is.True);
      Assert.That(result.Counters.MutationTestExecutions, Is.Zero);
      Assert.That(result.Mutants.Where(m => m.Status != MutationStatus.NoCoverage).All(m => m.Status == MutationStatus.Pending), Is.True);
      Assert.That(result.TestToSites[throwingTest.Id], Does.Contain(throwingMutation.SiteId));
      Assert.That(result.CoverageStatuses.Values, Is.All.EqualTo(CoverageStatus.CoverageSuccess));
      Assert.That(File.Exists(result.JsonReportPath), Is.True);
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task PerBatchCoverageCollectorPreservesPerTestCoverageWithOneBaselineHost()
  {
    var output = NewOutputDirectory();
    try
    {
      var result = await MutationEngine.RunAsync(Options(2, output, MutationExecutionMode.Batched, TestHostIsolationPolicy.PerBatch));
      Assert.That(result.OverallStatus, Is.EqualTo("Completed"));
      Assert.That(result.Counters.BaselineTestExecutions, Is.EqualTo(result.Tests.Count));
      Assert.That(result.CoverageStatuses, Has.Count.EqualTo(result.Tests.Count));
      Assert.That(result.CoverageStatuses.Values, Is.All.EqualTo(CoverageStatus.CoverageSuccess));
      Assert.That(result.TestToSites.Keys.Order(StringComparer.Ordinal),
          Is.EqualTo(result.Tests.Select(test => test.Id).Order(StringComparer.Ordinal)));
      Assert.That(result.Counters.TestHostProcessStarts, Is.EqualTo(result.Counters.MutationBatches + 2));
      Assert.That(result.Diagnostics, Has.Some.Contains("BaselineCoverageCollector"));
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task IsolatedBatchedAndAutoModesProduceEquivalentResults()
  {
    MutationRunResult? reference = null;
    foreach (var (mode, isolation) in new[]
    {
      (MutationExecutionMode.Isolated, TestHostIsolationPolicy.PerTest),
      (MutationExecutionMode.Batched, TestHostIsolationPolicy.PerTest),
      (MutationExecutionMode.Auto, TestHostIsolationPolicy.PerTest),
      (MutationExecutionMode.Batched, TestHostIsolationPolicy.PerBatch)
    })
    {
      var output = NewOutputDirectory();
      try
      {
        var result = await MutationEngine.RunAsync(Options(2, output, mode, isolation));
        Assert.That(result.OverallStatus, Is.EqualTo("Completed"));
        Assert.That(result.Mutants.Count, Is.EqualTo(34));
        Assert.That(result.KilledCount, Is.EqualTo(25));
        Assert.That(result.SurvivedCount, Is.EqualTo(8));
        Assert.That(result.NoCoverageCount, Is.EqualTo(1));
        Assert.That(result.Counters.BaselineTestExecutions, Is.EqualTo(18));
        Assert.That(result.CoverageStatuses, Has.Count.EqualTo(18));
        Assert.That(result.CoverageStatuses.Values, Is.All.EqualTo(CoverageStatus.CoverageSuccess));
        Assert.That(result.Counters.MutationTestExecutions, Is.GreaterThan(0));
        Assert.That(result.ExecutionPlan, Is.Not.Null);
        Assert.That(result.ExecutionProfiles, Has.Count.EqualTo(result.Tests.Count));
        Assert.That(result.ExecutionProfiles.All(profile => profile.IsolationRequired == (isolation != TestHostIsolationPolicy.PerBatch) && profile.TimeoutSeconds == 120), Is.True);
        Assert.That(result.ExecutionProfiles.All(profile => profile.FixtureAffinity != null), Is.True);
        Assert.That(result.ExecutionPlan!.SelectedMode, Is.EqualTo(mode == MutationExecutionMode.Auto ? MutationExecutionMode.Isolated : mode));
        Assert.That(result.Counters.MutationBatches, Is.EqualTo(result.ExecutionPlan.Batches.Count));
        Assert.That(result.ExecutionPlan.Batches.SelectMany(batch => batch.MutantIds).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(33));
        Assert.That(result.Counters.TestHostProcessStarts, Is.EqualTo(isolation == TestHostIsolationPolicy.PerBatch
            ? result.Counters.MutationBatches + 2
            : result.Counters.TotalTests + result.Counters.MutationTestExecutions + 1));
        if (isolation == TestHostIsolationPolicy.PerBatch)
        {
          Assert.That(result.ExecutionProfiles.All(profile => profile.TestHostIsolationPolicy == TestHostIsolationPolicy.PerBatch &&
              profile.TimeoutPolicy == TestRunTimeoutPolicy.AggregatePerBatch &&
              profile.TestsSerializedInSharedHost &&
              profile.EligibleExecutionModes.SequenceEqual([MutationExecutionMode.Batched])), Is.True);
          Assert.That(result.Counters.TestHostProcessStarts,
              Is.LessThan(result.Counters.TotalTests + result.Counters.MutationTestExecutions + 1));
        }
        Assert.That(result.Mutants.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(result.Mutants.Count));
        Assert.That(File.Exists(result.JsonReportPath), Is.True);
        Assert.That(File.Exists(result.HtmlReportPath), Is.True);

        if (reference is null)
        {
          reference = result;
        }
        else
        {
          Assert.That(result.Mutants.Select(m => $"{m.Id}|{m.Status}").Order(StringComparer.Ordinal),
              Is.EqualTo(reference.Mutants.Select(m => $"{m.Id}|{m.Status}").Order(StringComparer.Ordinal)));
          if (isolation == TestHostIsolationPolicy.PerBatch)
          {
            Assert.That(result.Counters.MutationTestExecutions, Is.EqualTo(result.ExecutionPlan!.Statistics.EstimatedTestExecutions));
          }
          else
          {
            Assert.That(result.Counters.MutationTestExecutions, Is.LessThanOrEqualTo(reference.Counters.MutationTestExecutions));
          }
          Assert.That(result.TestToSites.Keys.Order(StringComparer.Ordinal), Is.EqualTo(reference.TestToSites.Keys.Order(StringComparer.Ordinal)));
          foreach (var testId in reference.TestToSites.Keys)
          {
            Assert.That(result.TestToSites[testId].Order(StringComparer.Ordinal), Is.EqualTo(reference.TestToSites[testId].Order(StringComparer.Ordinal)));
          }
        }
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

  [Test]
  [Category("EndToEnd")]
  public async Task PerMutantTestHostPolicyReducesHostStartsAndPreservesMutationOutcomes()
  {
    var output = NewOutputDirectory();
    try
    {
      var result = await MutationEngine.RunAsync(Options(2, output, MutationExecutionMode.Isolated, TestHostIsolationPolicy.PerMutant));

      Assert.That(result.OverallStatus, Is.EqualTo("Completed"));
      Assert.That(result.Mutants.Count, Is.EqualTo(34));
      Assert.That(result.KilledCount, Is.EqualTo(25));
      Assert.That(result.SurvivedCount, Is.EqualTo(8));
      Assert.That(result.NoCoverageCount, Is.EqualTo(1));
      Assert.That(result.Mutants.All(mutant => mutant.Status is MutationStatus.Killed or MutationStatus.Survived or MutationStatus.NoCoverage), Is.True);
      Assert.That(result.ExecutionProfiles.All(profile => !profile.IsolationRequired &&
          profile.TestHostIsolationPolicy == TestHostIsolationPolicy.PerMutant), Is.True);
      Assert.That(result.ExecutionProfiles.All(profile => profile.TimeoutPolicy == TestRunTimeoutPolicy.AggregatePerMutant), Is.True);
      Assert.That(result.Diagnostics, Has.Some.Contains("BaselineCoverageCollector"));
      Assert.That(result.Counters.TestHostProcessStarts,
          Is.LessThanOrEqualTo(2 + result.Counters.TotalMutants - result.Counters.UncoveredMutants));
      Assert.That(result.Counters.TestHostProcessStarts,
          Is.LessThan(result.Counters.TotalTests + result.Counters.MutationTestExecutions + 1));
      Assert.That(result.ReportSchemaVersion, Is.EqualTo(4));
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task PerMutantExecutionStopsAfterTheFirstAttributableFailure()
  {
    var project = Path.Combine(Root, "benchmarks", "IsolationSample", "IsolationSample.csproj");
    var tests = Path.Combine(Root, "benchmarks", "IsolationSample.Tests", "IsolationSample.Tests.csproj");
    var output = NewOutputDirectory();
    var marker = Path.Combine(Path.GetTempPath(), "MutagenPerMutantBail", Guid.NewGuid().ToString("N"), "later-test-ran.txt");
    var previousMarker = Environment.GetEnvironmentVariable("MUTAGEN_PER_MUTANT_BAIL_MARKER");
    try
    {
      var discoveryOptions = new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [tests],
        TestFilter = "MutantBailoutTests",
        Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
        TestHostIsolationPolicy = TestHostIsolationPolicy.PerMutant,
        ExecutionMode = MutationExecutionMode.Isolated,
        MaxWorkers = 1,
        TimeoutSeconds = 120
      };
      var discovery = await MutationEngine.DiscoverAsync(discoveryOptions);
      Assert.That(discovery.Tests, Has.Count.EqualTo(2));
      var locks = discovery.Tests.ToDictionary(test => test.Id,
          _ => (IReadOnlyList<string>)["mutant-bailout"], StringComparer.Ordinal);
      Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
      Environment.SetEnvironmentVariable("MUTAGEN_PER_MUTANT_BAIL_MARKER", marker);

      var result = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [tests],
        TestFilter = "MutantBailoutTests",
        Mutators = discoveryOptions.Mutators,
        TestHostIsolationPolicy = TestHostIsolationPolicy.PerMutant,
        ExecutionMode = MutationExecutionMode.Isolated,
        ResourceLocksByTestId = locks,
        MaxWorkers = 1,
        TimeoutSeconds = 120,
        OutputDirectory = output
      });

      Assert.That(result.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, result.Diagnostics));
      Assert.That(result.Mutants, Is.Not.Empty);
      Assert.That(result.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True);
      Assert.That(result.Mutants.All(mutant => mutant.KillingTests.Any(name => name.Contains("BoundaryComparisonKillsEveryRelationalMutant", StringComparison.Ordinal))), Is.True);
      Assert.That(result.Counters.MutationTestExecutions, Is.EqualTo(result.Mutants.Count));
      Assert.That(File.Exists(marker), Is.False, "The later covered case must not run after the first test kills each mutant.");
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTAGEN_PER_MUTANT_BAIL_MARKER", previousMarker);
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }

      var markerDirectory = Path.GetDirectoryName(marker)!;
      if (Directory.Exists(markerDirectory))
      {
        Directory.Delete(markerDirectory, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task SqliteIntegrationFixtureUsesDiscoveredShardLocksEndToEnd()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenSqliteLockE2E", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var previousDatabaseRoot = Environment.GetEnvironmentVariable("MUTAGEN_ISOLATION_SQLITE_ROOT");
    Environment.SetEnvironmentVariable("MUTAGEN_ISOLATION_SQLITE_ROOT", Path.Combine(root, "database"));
    var productionProject = Path.Combine(Root, "benchmarks", "IsolationSample", "IsolationSample.csproj");
    var testProject = Path.Combine(Root, "benchmarks", "IsolationSample.Tests", "IsolationSample.Tests.csproj");
    var sharedOptions = new MutationOptions
    {
      ProjectPath = productionProject,
      TestProjects = [testProject],
      Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
      MaxWorkers = 4,
      TimeoutSeconds = 120,
      TestFilter = "SqliteShardExercisesTheSharedMutationSite",
      CacheDirectory = Path.Combine(root, "cache")
    };

    try
    {
      var discovery = await MutationEngine.DiscoverAsync(sharedOptions);
      Assert.That(discovery.Tests, Has.Length.EqualTo(2));
      var lockMap = discovery.Tests.ToDictionary(test => test.Id, test =>
      {
        var opening = test.DisplayName.LastIndexOf('(');
        var closing = test.DisplayName.LastIndexOf(')');
        Assert.That(opening, Is.GreaterThan(0), $"Unexpected isolation test display name: {test.DisplayName}");
        Assert.That(closing, Is.GreaterThan(opening), $"Unexpected isolation test display name: {test.DisplayName}");
        var group = int.Parse(test.DisplayName.AsSpan(opening + 1, closing - opening - 1), System.Globalization.CultureInfo.InvariantCulture);
        return (IReadOnlyList<string>)[$"sqlite-shard-{group % 4}"];
      }, StringComparer.Ordinal);
      var independentSelected = discovery.Tests.ToArray();
      Assert.That(independentSelected, Has.Length.EqualTo(2));
      async Task<MutationRunResult> RunIndependentResourcePolicy(TestHostIsolationPolicy isolationPolicy)
      {
        return await MutationEngine.RunAsync(new MutationOptions
        {
          ProjectPath = productionProject,
          TestProjects = [testProject],
          Mutators = sharedOptions.Mutators,
          MaxWorkers = 4,
          TimeoutSeconds = 120,
          ExecutionMode = MutationExecutionMode.Batched,
          TestHostIsolationPolicy = isolationPolicy,
          TestFilter = string.Join(';', independentSelected.Select(test => test.DisplayName)),
          ResourceLocksByTestId = lockMap,
          CacheDirectory = sharedOptions.CacheDirectory,
          OutputDirectory = Path.Combine(root, $"report-{isolationPolicy}")
        });
      }

      var batchedReference = await RunIndependentResourcePolicy(TestHostIsolationPolicy.PerBatch);
      var resourceGroupResult = await RunIndependentResourcePolicy(TestHostIsolationPolicy.PerResourceGroup);
      Assert.That(batchedReference.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, batchedReference.Diagnostics));
      Assert.That(resourceGroupResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, resourceGroupResult.Diagnostics));
      Assert.That(resourceGroupResult.Mutants.Select(mutation => mutation.Status),
          Is.EquivalentTo([MutationStatus.Killed, MutationStatus.Survived]));
      Assert.That(resourceGroupResult.Mutants.Select(mutant => $"{mutant.Id}|{mutant.Status}").Order(StringComparer.Ordinal),
          Is.EqualTo(batchedReference.Mutants.Select(mutant => $"{mutant.Id}|{mutant.Status}").Order(StringComparer.Ordinal)));
      Assert.That(resourceGroupResult.TestToSites.Keys.Order(StringComparer.Ordinal),
          Is.EqualTo(batchedReference.TestToSites.Keys.Order(StringComparer.Ordinal)));
      foreach (var testId in batchedReference.TestToSites.Keys)
      {
        Assert.That(resourceGroupResult.TestToSites[testId].Order(StringComparer.Ordinal),
            Is.EqualTo(batchedReference.TestToSites[testId].Order(StringComparer.Ordinal)));
      }
      Assert.That(resourceGroupResult.Counters.MutationTestExecutions,
          Is.EqualTo(resourceGroupResult.ExecutionPlan!.Statistics.EstimatedTestExecutions));
      Assert.That(resourceGroupResult.Diagnostics, Has.Some.Contains("ResourceGroupScheduling"));
      Assert.That(resourceGroupResult.ExecutionProfiles.Select(profile => profile.ResourceIsolationGroup)
          .Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(2));
      Assert.That(resourceGroupResult.ExecutionProfiles.All(profile => !profile.IsolationRequired &&
          profile.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup &&
          profile.TimeoutPolicy == TestRunTimeoutPolicy.AggregatePerBatch &&
          profile.TestsSerializedInSharedHost &&
          profile.EligibleExecutionModes.SequenceEqual([MutationExecutionMode.Batched])), Is.True);
      Assert.That(resourceGroupResult.Counters.TestHostProcessStarts, Is.GreaterThan(2));
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTAGEN_ISOLATION_SQLITE_ROOT", previousDatabaseRoot);
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

}
