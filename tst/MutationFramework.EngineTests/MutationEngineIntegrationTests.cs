using MutationFramework.Sdk;
using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class MutationEngineIntegrationTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static string AppProject => Path.Combine(Root, "samples", "SampleApplication", "SampleApplication.csproj");
    private static string TestProject => Path.Combine(Root, "samples", "SampleApplication.Tests", "SampleApplication.Tests.csproj");

    [Test]
    public async Task DiscoverUsesStructuredCaseIdentityAndRejectsUnsupportedOperators()
    {
        var result = await MutationEngine.DiscoverAsync(Options(1));

        Assert.That(result.Tests, Is.Not.Empty);
        Assert.That(result.Tests.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(result.Tests.Count));
        Assert.That(result.Tests.Where(t => t.Name.Contains("IsAdult_HandlesAgeBoundary", StringComparison.Ordinal)).Count(), Is.EqualTo(3));
        Assert.That(result.Tests.Where(t => t.Name.Contains("AddAsync_RunsWithFixtureAndStaticInitialization", StringComparison.Ordinal)).Count(), Is.EqualTo(2));
        Assert.That(result.Tests.All(t => t.ExecutorUri == "executor://nunit3testexecutor/"), Is.True);
        Assert.That(result.Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(result.Candidates.Count));
        Assert.That(result.Diagnostics, Has.Some.Contains("string concatenation"));
        Assert.That(result.Diagnostics, Has.Some.Contains("user-defined operators"));
        Assert.That(result.Diagnostics, Has.Some.Contains("dynamic"));
        Assert.That(result.Diagnostics, Has.Some.Contains("expression-tree"));
        Assert.That(result.Diagnostics, Has.Some.Contains("checked arithmetic"));
    }

    [Test]
    public void RuntimeIndicesAreUniqueAndStableIdsRemainFullLength()
    {
        var diagnostics = new List<string>();
        var candidates = Engine.DiscoverCandidates([Path.Combine(Root, "samples", "SampleApplication", "Calculator.cs")], AppProject, Options(1), diagnostics, CancellationToken.None);

        Assert.That(candidates.Select(c => c.MutantRuntimeId).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(candidates.Count));
        Assert.That(candidates.GroupBy(c => c.SiteId).All(group => group.Select(c => c.SiteRuntimeId).Distinct(StringComparer.Ordinal).Count() == 1), Is.True);
        Assert.That(candidates.Select(c => (c.SiteId, c.SiteRuntimeId)).Distinct().Count(), Is.EqualTo(candidates.Select(c => c.SiteId).Distinct(StringComparer.Ordinal).Count()));
        Assert.That(candidates.All(c => c.Id.Length == 64 && c.SiteId.Length == 64), Is.True);
    }

    [Test]
    public void ProbeReaderDistinguishesEmptySuccessFromMissingAndCorruptArtifacts()
    {
        var candidate = new Candidate("mutant", "site", "Equality", "sample.cs", 0, 1, 1, 1, "a > b", "a >= b", ">=", "0", "0");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var missing = Assert.Throws<CoverageCollectionException>(() => Engine.ReadProbeSites(path, [candidate]));
            Assert.That(missing!.Status, Is.EqualTo(CoverageStatus.CoverageUnavailable));
            File.WriteAllText(path, "not-json");
            var corrupt = Assert.Throws<CoverageCollectionException>(() => Engine.ReadProbeSites(path, [candidate]));
            Assert.That(corrupt!.Status, Is.EqualTo(CoverageStatus.CoverageCorrupted));
            File.WriteAllText(path, "{\"siteIds\":[],\"serializationMs\":0}");
            Assert.That(Engine.ReadProbeSites(path, [candidate]).Sites, Is.Empty);
            File.WriteAllText(path, "{\"siteIds\":[0],\"serializationMs\":2}");
            var hit = Engine.ReadProbeSites(path, [candidate]);
            Assert.That(hit.Sites, Is.EqualTo(new[] { "site" }));
            Assert.That(hit.SerializationMs, Is.EqualTo(2));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
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
    public void AutoPlannerFallsBackToIsolatedWhenCoverageIsIncomplete()
    {
        var items = new[] { new MutationWorkItem("M1", ["T1"], 1), new MutationWorkItem("M2", ["T2"], 1) };

        var plan = MutationPlanner.Build(items, [], MutationExecutionMode.Auto, coverageComplete: false, timeoutSeconds: 30, maxWorkers: 2);

        Assert.That(plan.SelectedMode, Is.EqualTo(MutationExecutionMode.Isolated));
        Assert.That(plan.Batches, Has.Count.EqualTo(2));
    }

    [Test]
    public void FailureAttributionRequiresEvidenceFromTheSelectedTestBody()
    {
        const string testName = "Sample.Tests.CalculatorTests.Add_ReturnsCorrectSum";

        Assert.That(TestPlatformSession.HasTestBodyFailureEvidence(testName,
            "at NUnit.Framework.Assert.That(...)\r\nat Sample.Tests.CalculatorTests.Add_ReturnsCorrectSum()"), Is.True);
        Assert.That(TestPlatformSession.HasTestBodyFailureEvidence(testName + "(18,True)",
            "at Sample.Tests.CalculatorTests.Add_ReturnsCorrectSum()"), Is.True);
        Assert.That(TestPlatformSession.HasTestBodyFailureEvidence(testName,
            "at Sample.Tests.CalculatorTests.SetUp()"), Is.False);
        Assert.That(TestPlatformSession.HasTestBodyFailureEvidence(testName, null), Is.False);
    }

    [Test]
    public void CompilerDiagnosticsMapOnlyTheResponsibleCandidateLines()
    {
        var root = Path.Combine(Path.GetTempPath(), "MutagenCompileDiagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Calculator.cs");
            File.WriteAllText(source, "class Calculator { int Add(int left, int right) => left + right; }");
            var invalid = new Candidate("bad", "site-1", "Arithmetic", "Calculator.cs", 44, 3, 1, 44,
                "left + right", "left - right", "-", "0", "0");
            var unrelated = invalid with { Id = "other", Line = 2 };
            string buildOutput = $"{source}(1,44): error CS0029: Cannot implicitly convert type\r\n{source}(1,2): error CS0103: unrelated error";

            var failures = Engine.FindInvalidCandidates(buildOutput, [invalid, unrelated], root);

            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(failures[0].Candidate.Id, Is.EqualTo("bad"));
            Assert.That(failures[0].Diagnostic, Does.Contain("CS0029"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void StatementEntryProbesAvoidLambdaForWholeReturnExpression()
    {
        var root = Path.Combine(Path.GetTempPath(), "MutagenProbeRewrite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "ProbeFixture.cs");
            var project = Path.Combine(root, "ProbeFixture.csproj");
            var target = Path.Combine(root, "instrumented", "ProbeFixture.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(project, "<Project />");
            File.WriteAllText(source, "public static class ProbeFixture { public static bool Check(System.Func<int> value, System.Func<bool> enabled) { return value() > 0 && enabled(); } }");
            File.Copy(source, target);
            var diagnostics = new List<string>();
            var candidates = Engine.DiscoverCandidates([source], project, Options(1), diagnostics, CancellationToken.None);

            Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root);
            string rewritten = File.ReadAllText(target);
            var returnRoot = candidates.Single(candidate => candidate.Original.Contains("&&", StringComparison.Ordinal));
            var nestedComparison = candidates.First(candidate => candidate.Operator == "Equality");

            Assert.That(rewritten, Does.Contain($"MutationRuntime.Hit({returnRoot.SiteRuntimeId})"));
            Assert.That(rewritten, Does.Contain($"MutationRuntime.Probe({nestedComparison.SiteRuntimeId}"));
            Assert.That(rewritten.IndexOf($"MutationRuntime.Hit({returnRoot.SiteRuntimeId})", StringComparison.Ordinal),
                Is.LessThan(rewritten.IndexOf("return", StringComparison.Ordinal)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task PlanKeepsThrowingExpressionCoveredWithoutExecutingMutants()
    {
        var output = NewOutputDirectory();
        try
        {
            var result = await MutationEngine.PlanAsync(Options(1, output));
            var throwingTest = result.Tests.Single(t => t.DisplayName == "ThrowingOperand_StillEntersCoverageSite");
            var throwingMutation = result.Mutants.First(m => m.Original.Contains("value() > 10", StringComparison.Ordinal));

            Assert.That(result.OverallStatus, Is.EqualTo("Planned"));
            Assert.That(result.Counters.MutationTestExecutions, Is.Zero);
            Assert.That(result.Mutants.Where(m => m.Status != MutationStatus.NoCoverage).All(m => m.Status == MutationStatus.Pending), Is.True);
            Assert.That(result.TestToSites[throwingTest.Id], Does.Contain(throwingMutation.SiteId));
            Assert.That(result.CoverageStatuses.Values, Is.All.EqualTo(CoverageStatus.CoverageSuccess));
            Assert.That(File.Exists(result.JsonReportPath), Is.True);
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }

    [Test]
    public async Task IsolatedBatchedAndAutoModesProduceEquivalentResults()
    {
        MutationRunResult? reference = null;
        foreach (var mode in new[] { MutationExecutionMode.Isolated, MutationExecutionMode.Batched, MutationExecutionMode.Auto })
        {
            var output = NewOutputDirectory();
            try
            {
                var result = await MutationEngine.RunAsync(Options(2, output, mode));
                    Assert.That(result.OverallStatus, Is.EqualTo("Completed"));
                    Assert.That(result.Mutants.Count, Is.EqualTo(18));
                    Assert.That(result.KilledCount, Is.EqualTo(10));
                    Assert.That(result.SurvivedCount, Is.EqualTo(7));
                    Assert.That(result.NoCoverageCount, Is.EqualTo(1));
                    Assert.That(result.Counters.BaselineTestExecutions, Is.EqualTo(18));
                    Assert.That(result.CoverageStatuses, Has.Count.EqualTo(18));
                    Assert.That(result.CoverageStatuses.Values, Is.All.EqualTo(CoverageStatus.CoverageSuccess));
                    Assert.That(result.Counters.MutationTestExecutions, Is.GreaterThan(0));
                    Assert.That(result.ExecutionPlan, Is.Not.Null);
                    Assert.That(result.ExecutionPlan!.SelectedMode, Is.EqualTo(mode == MutationExecutionMode.Auto ? MutationExecutionMode.Isolated : mode));
                    Assert.That(result.Counters.MutationBatches, Is.EqualTo(result.ExecutionPlan.Batches.Count));
                    Assert.That(result.ExecutionPlan.Batches.SelectMany(batch => batch.MutantIds).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(17));
                    Assert.That(result.Counters.TestHostProcessStarts, Is.EqualTo(result.Counters.TotalTests + result.Counters.MutationTestExecutions + 1));
                    Assert.That(result.Mutants.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(result.Mutants.Count));
                    Assert.That(File.Exists(result.JsonReportPath), Is.True);
                    Assert.That(File.Exists(result.HtmlReportPath), Is.True);

                    if (reference is null) reference = result;
                    else
                    {
                        Assert.That(result.Mutants.Select(m => $"{m.Id}|{m.Status}").Order(StringComparer.Ordinal),
                            Is.EqualTo(reference.Mutants.Select(m => $"{m.Id}|{m.Status}").Order(StringComparer.Ordinal)));
                        Assert.That(result.Counters.MutationTestExecutions, Is.EqualTo(reference.Counters.MutationTestExecutions));
                        Assert.That(result.TestToSites.Keys.Order(StringComparer.Ordinal), Is.EqualTo(reference.TestToSites.Keys.Order(StringComparer.Ordinal)));
                        foreach (var testId in reference.TestToSites.Keys)
                            Assert.That(result.TestToSites[testId].Order(StringComparer.Ordinal), Is.EqualTo(reference.TestToSites[testId].Order(StringComparer.Ordinal)));
                    }
            }
            finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
        }
    }

    private static MutationOptions Options(int workers, string? output = null, MutationExecutionMode mode = MutationExecutionMode.Auto) => new()
    {
        ProjectPath = AppProject,
        TestProjects = [TestProject],
        MaxWorkers = workers,
        OutputDirectory = output ?? NewOutputDirectory(),
        ExecutionMode = mode,
        TimeoutSeconds = 120
    };

    private static string NewOutputDirectory() => Path.Combine(Path.GetTempPath(), "MutationFramework.EngineTests", Guid.NewGuid().ToString("N"));

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "MutationFramework.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("Unable to locate MutationFramework.slnx from the test output directory.");
    }
}
