using Microsoft.VisualStudio.TestPlatform.ObjectModel;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class OutcomeClassificationTests
{
  [Test]
  [Category("Unit")]
  public void PostKillCancellationDistinguishesStartedAndUnstartedSelectedTests()
  {
    static TestCaseDescriptor Case(string name)
    {
      var native = new TestCase(name, new Uri("executor://unit"), "tests.dll");
      return new(native, name, name, name, native.ExecutorUri.ToString(), "tests.dll");
    }
    var completed = Case("completed");
    var started = Case("started");
    var unstarted = Case("unstarted");
    var results = TestResultClassifier.PartialResults([completed, started, unstarted],
        [new TestResult(completed.Case) { Outcome = TestOutcome.Passed }], false,
        startedTests: new HashSet<Guid> { completed.Case.Id, started.Case.Id });
    Assert.Multiple(() =>
    {
      Assert.That(results[completed.Id].State, Is.EqualTo(TestExecutionState.Completed));
      Assert.That(results[started.Id].State, Is.EqualTo(TestExecutionState.Canceled));
      Assert.That(results[started.Id].FailureKind, Is.EqualTo(TestFailureKind.Bailed));
      Assert.That(results[started.Id].ExecutionObserved, Is.True);
      Assert.That(results[unstarted.Id].State, Is.EqualTo(TestExecutionState.NotExecuted));
      Assert.That(results[unstarted.Id].ExecutionObserved, Is.False);
      Assert.That(results.Values.Count(result => result.ExecutionObserved), Is.EqualTo(2));
    });
  }

  [TestCase((int)TestFailureKind.Infrastructure, MutationStatus.InfrastructureError)]
  [TestCase((int)TestFailureKind.Timeout, MutationStatus.Timeout)]
  [TestCase((int)TestFailureKind.Unattributed, MutationStatus.Inconclusive)]
  [TestCase((int)TestFailureKind.Canceled, MutationStatus.Inconclusive)]
  [TestCase((int)TestFailureKind.Bailed, MutationStatus.Inconclusive)]
  [TestCase((int)TestFailureKind.TestBody, MutationStatus.Killed)]
  [TestCase((int)TestFailureKind.None, MutationStatus.Survived)]
  [Category("Unit")]
  public void ClassificationUsesTypedFailureRatherThanDiagnosticText(int failureKind, MutationStatus expected)
  {
    var kind = (TestFailureKind)failureKind;
    var run = new TestRun("test", kind, 12, "TIMEOUT: misleading diagnostic");
    var outcome = MutationOutcomeClassifier.ClassifyBatch([run]);
    Assert.That(outcome.Status, Is.EqualTo(expected));
    Assert.That(outcome.DurationMs, Is.EqualTo(12));
  }

  [Test]
  [Category("Unit")]
  public void ACompleteBatchAndAStoppedMutantPreserveTheirDifferentAttributionRules()
  {
    var killed = new TestRun("killing test", TestFailureKind.TestBody, 4, "assertion");
    var uncertain = new TestRun("skipped test", TestFailureKind.Skipped, 2, "skipped");
    var batch = MutationOutcomeClassifier.ClassifyBatch([killed, uncertain]);
    var mutant = MutationOutcomeClassifier.ClassifyMutant([killed, uncertain]);
    Assert.That(batch.Status, Is.EqualTo(MutationStatus.Inconclusive));
    Assert.That(batch.Diagnostic, Is.EqualTo("skipped"));
    Assert.That(mutant.Status, Is.EqualTo(MutationStatus.Killed));
    Assert.That(mutant.Diagnostic, Is.EqualTo("assertion"));
    Assert.That(mutant.KillingTests, Is.EqualTo(new[] { "killing test" }));
  }

  [Test]
  [Category("Unit")]
  public void BailedTestsAfterAKillDoNotMakeTheMutantInconclusive()
  {
    var killed = new TestRun("killing test", TestFailureKind.TestBody, 4, "assertion");
    var bailed = new TestRun("unstarted test", TestFailureKind.Bailed, 0, "bailed");
    Assert.That(MutationOutcomeClassifier.ClassifyMutant([killed, bailed]).Status, Is.EqualTo(MutationStatus.Killed));
  }

  [Test]
  [Category("Unit")]
  public void UnpinnedSdkSelectionUsesNumericVersionsAndPrefersAStableRelease()
  {
    var candidates = new[] { "7.0.100", "9.0.100", "11.0.100-preview.1", "11.0.100", "not-an-sdk" };
    Assert.That(TestPlatformPaths.SelectSdk(candidates, null), Is.EqualTo("11.0.100"));
    Assert.That(TestPlatformPaths.SelectSdk(candidates, "7.0.100"), Is.EqualTo("7.0.100"));
    Assert.That(TestPlatformPaths.SelectSdk(candidates, "8.0.100"), Is.Null);
  }

  [TestCase(false, "Planned")]
  [TestCase(true, "CompletedWithErrors")]
  [Category("Unit")]
  public async Task ACompileRejectedCandidateRetainsItsResultAfterItLeavesTheExecutableCandidateList(bool execute, string expectedStatus)
  {
    var rejected = new MutationResult("rejected", "site", "Arithmetic", "source.cs", 1, 1, "a + b", "invalid()",
        MutationStatus.CompileError, [], [], Diagnostic: "CS0103: invalid replacement");
    var executionPlan = MutationPlanner.Build([], [rejected], MutationExecutionMode.Isolated, true, 30, 1);
    var plan = new PlannedMutations(executionPlan, [], [], [], true);
    var baseline = new BaselineCoverage([], new(StringComparer.Ordinal), new Dictionary<string, ResourceGroup>(StringComparer.Ordinal),
        new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), 0, 0, 0, 0, 0);
    var options = new MutationOptions { ProjectPath = "source.csproj", TestProjects = ["tests.csproj"] };
    var metrics = new RunMetrics();
    using var locks = new ResourceLockManager();
    var context = new MutationExecutionContext(options, Path.GetTempPath(), "unused-console", locks, metrics, []);
    var cache = new ValidatedMutationResults(new ArtifactCache(Path.GetTempPath()), "unused", new Dictionary<string, string>(),
        new Dictionary<string, MutationResult>());

    var execution = await MutationExecutor.ExecuteAsync([], baseline, plan, cache, context, execute, CancellationToken.None);
    var result = MutationResultAggregator.Create(options, baseline, plan, execution,
        new(new(0, 0, 0, 0, 0, 0, 0), metrics, 0), [], []);

    Assert.That(result.Mutants, Is.EqualTo(new[] { rejected }));
    Assert.That(result.OverallStatus, Is.EqualTo(expectedStatus));
    Assert.That(result.Counters.MutationTestExecutions, Is.Zero);
  }
}
