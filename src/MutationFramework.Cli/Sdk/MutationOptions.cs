namespace MutationFramework.Sdk;

public sealed class MutationOptions
{
  public required string ProjectPath
  {
    get; init;
  }
  public IReadOnlyList<string> ProductionProjects { get; init; } = [];
  public required IReadOnlyList<string> TestProjects
  {
    get; init;
  }
  public int MaxWorkers { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
  public string OutputDirectory { get; init; } = "mutation-results";
  public string Report { get; init; } = "json,html";
  public int TimeoutSeconds { get; init; } = 120;
  public int MaxRunTimeoutSeconds { get; init; } = 1800;
  public int HostStartupTimeoutSeconds { get; init; } = 60;
  public bool PrioritizeFastTests { get; init; } = true;
  public int ProgressIntervalSeconds { get; init; } = 10;
  public string? CheckpointDirectory
  {
    get; init;
  }
  public string? CheckpointScopeKey
  {
    get; init;
  }
  /// <summary>Loads a complete native Mutagen report as the starting point for an incremental run.</summary>
  public string? BaselinePath
  {
    get; init;
  }
  /// <summary>Saves a complete native Mutagen report and integrity metadata for later incremental runs.</summary>
  public string? SaveBaselinePath
  {
    get; init;
  }
  /// <summary>Caller attestation that external state affecting test outcomes is unchanged between runs.</summary>
  public string? BaselineScopeKey
  {
    get; init;
  }
  public MutationExecutionMode ExecutionMode { get; init; } = MutationExecutionMode.Auto;
  /// <summary>PerMutant reuses one host for tests of one mutant; PerBatch mixes coverage-disjoint mutants in one testhost; PerResourceGroup runs disjoint declared resource groups in separate hosts. Reuse requires caller attestation of same-process equivalence within each reused host and independent process equivalence across resource groups.</summary>
  public TestHostIsolationPolicy TestHostIsolationPolicy { get; init; } = TestHostIsolationPolicy.PerMutant;
  public string? TestFilter
  {
    get; init;
  }
  public string? Include
  {
    get; init;
  }
  public string? Exclude
  {
    get; init;
  }
  public IReadOnlySet<string>? Mutators
  {
    get; init;
  }
  public string? TargetFramework
  {
    get; init;
  }
  public string? CacheDirectory
  {
    get; init;
  }
  /// <summary>
  /// Enables validated per-mutant result reuse when this stable scope key changes whenever external test state can affect outcomes.
  /// Leave null to always execute mutants. Result reuse is restricted to per-test isolated execution.
  /// </summary>
  public string? MutationResultCacheScopeKey
  {
    get; init;
  }
  public IReadOnlyDictionary<string, IReadOnlyList<string>> ResourceLocksByTestId { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
  /// <summary>Optional explicit mutator registry for SDK extensions.</summary>
  public MutatorRegistry? Registry
  {
    get; init;
  }
}
