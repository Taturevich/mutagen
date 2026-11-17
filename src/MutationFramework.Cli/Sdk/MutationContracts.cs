namespace MutationFramework.Sdk;

public enum MutationStatus { Pending, Killed, Survived, NoCoverage, CompileError, Unsupported, Timeout, InfrastructureError, Inconclusive }
public enum CoverageStatus { CoverageSuccess, CoverageUnavailable, CoverageCorrupted, CoverageCollectionFailed }
public enum TestRunTimeoutPolicy { PerTest, AggregatePerMutant, AggregatePerBatch, Mixed }
/// <summary>Controls whether tests for one mutant may share a testhost process. PerMutant matches grouped test-run behavior.</summary>
public enum TestHostIsolationPolicy { PerTest, PerMutant, PerBatch, PerResourceGroup, Mixed }
public sealed record TestIdentity(string Id, string Name, string DisplayName, long DurationMs = 0, string ExecutorUri = "", string SourceAssembly = "", string? ParameterizedCaseIdentity = null);
public sealed record TestExecutionProfile(string TestId, long BaselineDurationMs, IReadOnlyList<string> CoveredMutationSites,
    string? FixtureAffinity, bool IsolationRequired, IReadOnlyList<string> ResourceLocks, int TimeoutSeconds,
    IReadOnlyList<MutationExecutionMode> EligibleExecutionModes)
{
  public TestHostIsolationPolicy TestHostIsolationPolicy { get; init; } = TestHostIsolationPolicy.PerMutant;
  public TestRunTimeoutPolicy TimeoutPolicy { get; init; } = TestRunTimeoutPolicy.PerTest;
  public bool TestsSerializedInSharedHost { get; init; }
  public string? ResourceIsolationGroup { get; init; }
}
public sealed record MutationResult(string Id, string SiteId, string Operator, string SourceFile, int Line, int Column, string Original, string Replacement, MutationStatus Status, IReadOnlyList<string> CoveringTests, IReadOnlyList<string> KillingTests, long DurationMs = 0, string? Diagnostic = null);
public sealed record PhaseTimings(long DiscoveryMs, long BuildMs, long BaselineMs, long CoverageMs, long MutationGenerationMs, long ExecutionMs, long ReportingMs)
{
  public long ProbeSerializationMs { get; init; }
  public long CoverageMappingMs { get; init; }
  public long ProjectEvaluationMs { get; init; }
  public long ProjectCopyMs { get; init; }
  public long InstrumentationMs { get; init; }
  public long MutationPlanningMs { get; init; }
  public long WorkerInitializationMs { get; init; }
}
public sealed record ExecutionCounters(int TotalMutants, int CoveredMutants, int UncoveredMutants, int TotalTests, int BaselineTestExecutions, int MutationTestExecutions, int TestRunnerProcessStarts, int TestDiscoveryOperations, int BuildInvocations, int WorkerRestarts, int WorkerFailures, int MutationBatches, double AverageMutantsPerBatch, long TotalWorkerBusyTimeMs, long TotalWorkerIdleTimeMs)
{
  public int TestHostProcessStarts { get; init; }
}
public sealed record MutationRunResult(string OverallStatus, IReadOnlyList<MutationResult> Mutants, int KilledCount, int SurvivedCount, int NoCoverageCount, double? MutationScore, PhaseTimings Timings, long TotalDurationMs, IReadOnlyDictionary<string, IReadOnlyList<string>> TestToSites, IReadOnlyList<string> Diagnostics, string JsonReportPath, string HtmlReportPath)
{
  public int ReportSchemaVersion { get; init; } = 4;
  public IReadOnlyList<MutationResult> UnsupportedMutations { get; init; } = [];
  public ExecutionCounters Counters { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
  public int ProcessStarts { get; init; }
  public IReadOnlyList<TestIdentity> Tests { get; init; } = [];
  public IReadOnlyList<TestExecutionProfile> ExecutionProfiles { get; init; } = [];
  public MutationExecutionPlan? ExecutionPlan { get; init; }
  public IReadOnlyDictionary<string, CoverageStatus> CoverageStatuses { get; init; } = new Dictionary<string, CoverageStatus>();
}
public sealed record MutationCandidateInfo(string Id, string SiteId, string Operator, string SourceFile, int Line, int Column, string Original, string Replacement);
public sealed record MutationDiscoveryResult(IReadOnlyList<MutationCandidateInfo> Candidates, IReadOnlyList<TestIdentity> Tests, IReadOnlyList<string> Diagnostics, long DurationMs, int ProcessStarts)
{
  public IReadOnlyList<MutationResult> UnsupportedMutations { get; init; } = [];
}
