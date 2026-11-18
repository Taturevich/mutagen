namespace MutationFramework.Sdk;

internal sealed record MutationOutcome(MutationStatus Status, string[] KillingTests, long DurationMs, string? Diagnostic);

internal static class MutationOutcomeClassifier
{
  internal static MutationOutcome ClassifyBatch(IReadOnlyList<TestRun> runs) => Classify(runs, stoppedAfterKill: false);

  internal static MutationOutcome ClassifyMutant(IReadOnlyList<TestRun> runs) => Classify(runs, stoppedAfterKill: true);

  private static MutationOutcome Classify(IReadOnlyList<TestRun> runs, bool stoppedAfterKill)
  {
    TestRun? infrastructure = null, timeout = null, uncertain = null, firstKill = null;
    var killingTests = new List<string>();
    long duration = 0;
    foreach (var run in runs)
    {
      duration += run.DurationMs;
      if (run.FailureKind == TestFailureKind.Infrastructure)
      {
        infrastructure ??= run;
      }
      if (run.FailureKind == TestFailureKind.Timeout)
      {
        timeout ??= run;
      }
      if ((!stoppedAfterKill || run.FailureKind != TestFailureKind.Bailed) &&
          (run.Skipped || run.FailureKind is TestFailureKind.Unattributed or TestFailureKind.Canceled))
      {
        uncertain ??= run;
      }
      if (run.FailureKind == TestFailureKind.TestBody)
      {
        firstKill ??= run;
        killingTests.Add(run.Name);
      }
    }

    if (firstKill is null && runs.Any(run => run.FailureKind == TestFailureKind.Bailed))
    {
      uncertain ??= runs.First(run => run.FailureKind == TestFailureKind.Bailed);
    }
    // A complete batch requires every responsible test to be attributable. A mutant run
    // deliberately canceled after its first attributable failure may contain bailed tests.
    var status = infrastructure is not null ? MutationStatus.InfrastructureError
        : timeout is not null ? MutationStatus.Timeout
        : !stoppedAfterKill && uncertain is not null ? MutationStatus.Inconclusive
        : firstKill is not null ? MutationStatus.Killed
        : uncertain is not null ? MutationStatus.Inconclusive
        : MutationStatus.Survived;
    var diagnostic = stoppedAfterKill
        ? status switch
        {
          MutationStatus.InfrastructureError => infrastructure?.Error,
          MutationStatus.Timeout => timeout?.Error,
          MutationStatus.Killed => firstKill?.Error,
          _ => uncertain?.Error
        }
        : infrastructure?.Error ?? timeout?.Error ?? uncertain?.Error ?? firstKill?.Error;
    if (stoppedAfterKill && status == MutationStatus.Inconclusive)
    {
      diagnostic ??= "INCONCLUSIVE: Selected test was skipped.";
    }
    // Per-mutant runs publish killing tests only when the final outcome is killed.
    return new(status, !stoppedAfterKill || status == MutationStatus.Killed ? killingTests.ToArray() : [], duration, diagnostic);
  }
}
