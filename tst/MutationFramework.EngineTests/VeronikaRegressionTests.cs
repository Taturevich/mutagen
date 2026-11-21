using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
[Category("Component")]
public sealed class VeronikaRegressionTests
{
  [Test]
  public void ProviderQueriesDoNotReceiveInTreeRuntimeCalls()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    Directory.CreateDirectory(directory);
    try
    {
      var source = Path.Combine(directory, "Product.cs");
      File.WriteAllText(source, """
          using System;
          using System.Linq;
          using System.Linq.Expressions;
          public static class Product
          {
            public static IQueryable<int> Query(IQueryable<int> source) => from value in source where value > 0 select value + 1;
            public static IQueryable<int> Method(IQueryable<int> source) => source.Where(value => value == 1);
            public static Expression<Func<int, bool>> Predicate() => value => value < 3;
            public static int Plain(int value) => value + 2;
            public static bool Nested(bool a, bool b, bool c) => a || b || c;
          }
          """);
      var unsupported = new List<MutationResult>();
      var candidates = MutationDiscovery.DiscoverCandidates([source], Path.Combine(directory, "Product.csproj"),
          new MutationOptions { ProjectPath = "Product.csproj", TestProjects = [] }, [], CancellationToken.None, unsupported);
      Assert.That(candidates.Any(candidate => candidate.Original is "value > 0" or "value + 1" or "value == 1" or "value < 3"), Is.False);
      Assert.That(candidates.Any(candidate => candidate.Original == "value + 2"), Is.True);
      Assert.That(candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(candidates.Count),
          "Nested expressions with a shared start and replacement require different identities.");
      Assert.That(candidates.Count(candidate => candidate.Operator == "Logical"), Is.GreaterThanOrEqualTo(2));
      Assert.That(unsupported.Any(candidate => candidate.Diagnostic?.Contains("provider expression-tree", StringComparison.Ordinal) == true), Is.True);
    }
    finally { Directory.Delete(directory, true); }
  }

  [Test]
  public async Task RevokedControllerCannotLaunchAfterItsStartupDeadline()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var oldLease = Environment.GetEnvironmentVariable(TestControllerLease.LeaseVariable);
    try
    {
      using var lease = new TestControllerLease(directory);
      var path = lease.EnvironmentVariables("unused")[TestControllerLease.LeaseVariable]!;
      lease.Terminate();
      Environment.SetEnvironmentVariable(TestControllerLease.LeaseVariable, path);
      Assert.That(await TestControllerLease.RunProxyAsync("must-not-be-launched.dll", []), Is.EqualTo(5));
      Assert.That(File.Exists(path), Is.False);
      Assert.That(TestControllerLease.ParentArgument("/parentprocessid:10", 22), Is.EqualTo("/parentprocessid:22"));
      Assert.That(TestControllerLease.ParentArgument("--parentprocessid:10", 22), Is.EqualTo("--parentprocessid:22"));
    }
    finally
    {
      Environment.SetEnvironmentVariable(TestControllerLease.LeaseVariable, oldLease);
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, true);
      }
    }
  }

  [Test]
  public void RestoredResourceGroupIsRemovedAfterBatchSplitting()
  {
    var locks = new Dictionary<string, IReadOnlyList<string>> { ["t1"] = ["r1"], ["t2"] = ["r2"] };
    var groups = ResourceGroupPlanner.CreateGroups(["t1", "t2"], locks);
    var batch = new MutationBatch(0, ["m1", "m2"], ["t1", "t2"], new Dictionary<string, string> { ["t1"] = "m1", ["t2"] = "m2" }, 2, true, 30);
    var execution = new MutationExecutionPlan([batch], [], new(2, 0, 0, 1, 2, 2), MutationExecutionMode.Batched, MutationExecutionMode.Batched);
    var plan = new PlannedMutations(execution, [new("m1", ["t1"], 1), new("m2", ["t2"], 1)], [], [], true);
    var baseline = new BaselineCoverage([], new(StringComparer.Ordinal), groups, new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), 0, 0, 0, 0, 0);
    var options = new MutationOptions { ProjectPath = "unused.csproj", TestProjects = [], TestHostIsolationPolicy = TestHostIsolationPolicy.PerResourceGroup, ExecutionMode = MutationExecutionMode.Batched, MaxWorkers = 2, ResourceLocksByTestId = locks };
    var completed = new MutationResult("m1", "s1", "Arithmetic", "Product.cs", 1, 1, "a+b", "a-b", MutationStatus.Survived, ["test"], []);
    var schedule = ResourceAwareScheduler.Create(plan, baseline, options, new Dictionary<string, MutationResult> { [completed.Id] = completed }, true, []);
    Assert.That(schedule.WorkUnits.SelectMany(unit => unit.MutantIds), Is.EqualTo(new[] { "m2" }));
  }

  [Test]
  public async Task CheckpointRestoresOnlyWholeValidatedUnitsAndRejectsChangedInputs()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    try
    {
      static MutationResult Result(string id) => new(id, "site", "Arithmetic", "Product.cs", 1, 1, "a+b", "a-b", MutationStatus.Survived, ["test"], []);
      var checkpoint = await RunCheckpoint.OpenAsync(directory, "inputs-one", [["a"], ["b", "c"]], CancellationToken.None);
      await checkpoint.StoreAsync([Result("a")], CancellationToken.None);
      _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await checkpoint.StoreAsync([Result("b")], CancellationToken.None));
      var resumed = await RunCheckpoint.OpenAsync(directory, "inputs-one", [["a"], ["b", "c"]], CancellationToken.None);
      Assert.That(resumed.Restored.Keys, Is.EquivalentTo(new[] { "a" }));
      await resumed.StoreAsync([Result("b"), Result("c")], CancellationToken.None);
      var completed = await RunCheckpoint.OpenAsync(directory, "inputs-one", [["a"], ["b", "c"]], CancellationToken.None);
      Assert.That(completed.Restored.Keys, Is.EquivalentTo(new[] { "a", "b", "c" }));
      var invalidated = await RunCheckpoint.OpenAsync(directory, "inputs-two", [["a"], ["b", "c"]], CancellationToken.None);
      Assert.That(invalidated.Restored, Is.Empty);
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  [Test]
  public async Task CheckpointInvalidatesOnlyUnitsWhoseResponsibleTestsChanged()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    try
    {
      var root = Path.GetFullPath(directory);
      TestCaseDescriptor Case(string id)
      {
        var test = new TestCase(id, new Uri("executor://nunit3testexecutor"), Path.Combine(root, "Tests.dll"));
        return new(test, id, id, id, test.ExecutorUri.ToString(), test.Source);
      }
      var tests = new[] { Case("t1"), Case("t2"), Case("t3") }.ToDictionary(test => test.Id);
      MutationBatch Batch(bool changed) => new(0, ["a", "b"], changed ? ["t1", "t2", "t3"] : ["t1", "t2"],
          changed ? new Dictionary<string, string> { ["t1"] = "a", ["t2"] = "b", ["t3"] = "b" }
              : new Dictionary<string, string> { ["t1"] = "a", ["t2"] = "b" }, 1, false, 30);
      Dictionary<string, string> Scopes(bool changed) => new[] { "a", "b" }.ToDictionary(
          id => ArtifactCache.ContentKey([], [id]), id => RunCheckpoint.UnitScope([id], Batch(changed), tests, root));
      var first = await RunCheckpoint.OpenAsync(root, "invariant", [["a"], ["b"]], CancellationToken.None, Scopes(false));
      foreach (var id in new[] { "a", "b" })
      {
        await first.StoreAsync([new(id, "site", "Arithmetic", "Product.cs", 1, 1, "a+b", "a-b", MutationStatus.Survived, [], [])], CancellationToken.None);
      }
      var resumed = await RunCheckpoint.OpenAsync(root, "invariant", [["a"], ["b"]], CancellationToken.None, Scopes(true));
      Assert.That(resumed.Restored.Keys, Is.EquivalentTo(new[] { "a" }));
      Assert.That(RunCheckpoint.UnitScope(["a", "b"], Batch(false), tests, root),
          Is.Not.EqualTo(RunCheckpoint.UnitScope(["a", "b"], Batch(true), tests, root)), "A shared host requires the whole unit's test membership.");
      Assert.That(RunCheckpoint.UnitScope(["a"], Batch(false), tests, root),
          Is.Not.EqualTo(RunCheckpoint.UnitScope(["a"], Batch(false), tests, root, new HashSet<string> { "t1" })),
          "Coverage drift elsewhere can change effective per-test isolation without changing this unit's membership.");
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  [Test]
  public void CheckpointScopeDetectsIsolationFallbackFromCoverageOfAnotherMutant()
  {
    var root = Path.GetFullPath(TestWorkspace.NewOutputDirectory());
    TestCaseDescriptor Case(string id, string executor)
    {
      var test = new TestCase(id, new Uri(executor), Path.Combine(root, "Tests.dll"));
      return new(test, id, id, id, test.ExecutorUri.ToString(), test.Source);
    }
    var cases = new[] { Case("t1", "executor://nunit3testexecutor"), Case("t2", "executor://custom"), Case("t3", "executor://nunit3testexecutor") };
    var tests = cases.ToDictionary(test => test.Id);
    var options = new MutationOptions
    {
      ProjectPath = "unused.csproj",
      TestProjects = [],
      ExecutionMode = MutationExecutionMode.Isolated,
      TestHostIsolationPolicy = TestHostIsolationPolicy.PerMutant,
      ResourceLocksByTestId = new Dictionary<string, IReadOnlyList<string>> { ["t2"] = ["resource"] }
    };
    var candidates = new[] { "a", "b" }.Select(id => new Candidate(id, "s" + id, "Arithmetic", "Product.cs", 0, 3, 1, 1, "a+b", "a-b", "-", id, id)).ToList();
    PlannedMutations Plan(bool changed)
    {
      var mapping = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
      {
        ["t1"] = changed ? ["sa", "sb"] : ["sa"],
        ["t2"] = ["sb"],
        ["t3"] = ["sa"]
      };
      var baseline = new BaselineCoverage(cases, tests, new Dictionary<string, ResourceGroup>(), mapping,
          cases.ToDictionary(test => test.Id, test => new TestRun(test.Id, TestFailureKind.None, 1, null)),
          new SortedDictionary<string, CoverageStatus>(cases.ToDictionary(test => test.Id, _ => CoverageStatus.CoverageSuccess), StringComparer.Ordinal), 0, 0, 0, 0, 0);
      return MutationPlanning.Create(candidates, new([], [], 0, 0), baseline, options, []);
    }
    var before = Plan(false);
    var after = Plan(true);
    var oldBatch = before.ExecutionPlan.Batches.Single(batch => batch.MutantIds.Contains("a"));
    var newBatch = after.ExecutionPlan.Batches.Single(batch => batch.MutantIds.Contains("a"));
    Assert.That(newBatch.TestIds, Is.EquivalentTo(oldBatch.TestIds));
    Assert.That(RunCheckpoint.UnitScope(["a"], oldBatch, tests, root, before.PerTestIsolationIds),
        Is.Not.EqualTo(RunCheckpoint.UnitScope(["a"], newBatch, tests, root, after.PerTestIsolationIds)));
  }

  [Test]
  public void CheckpointNormalizesOnlyKnownMvcManifestWorkspacePaths()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    try
    {
      var roots = new[] { Path.Combine(directory, "one"), Path.Combine(directory, "two") };
      foreach (var root in roots)
      {
        Directory.CreateDirectory(root);
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["Api"] = Path.Combine(root, "src", "Api") });
        File.WriteAllText(Path.Combine(root, "MvcTestingAppManifest.json"), json);
        File.WriteAllText(Path.Combine(root, "settings.json"), json);
        File.WriteAllText(Path.Combine(root, "Api.staticwebassets.runtime.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
          ContentRoots = new[] { Path.Combine(root, "wwwroot") },
          Root = new
          {
            Children = new
            {
              File = new
              {
                Asset = new
                {
                  ContentRootIndex = 0,
                  SubPath = "file.txt"
                }
              }
            }
          }
        }));
        File.WriteAllText(Path.Combine(root, "Api.staticwebassets.endpoints.json"), json);
      }
      string Hash(int index, string file) => RunCheckpoint.RuntimeDependencyHash(Path.Combine(roots[index], file), roots[index]);
      Assert.That(Hash(0, "MvcTestingAppManifest.json"), Is.EqualTo(Hash(1, "MvcTestingAppManifest.json")));
      Assert.That(Hash(0, "settings.json"), Is.Not.EqualTo(Hash(1, "settings.json")));
      Assert.That(Hash(0, "Api.staticwebassets.runtime.json"), Is.EqualTo(Hash(1, "Api.staticwebassets.runtime.json")));
      Assert.That(Hash(0, "Api.staticwebassets.endpoints.json"), Is.Not.EqualTo(Hash(1, "Api.staticwebassets.endpoints.json")),
          "Endpoint headers such as Last-Modified remain observable runtime inputs.");
      File.WriteAllText(Path.Combine(roots[1], "MvcTestingAppManifest.json"), "{\"Api\":\"external-value\"}");
      Assert.That(Hash(0, "MvcTestingAppManifest.json"), Is.Not.EqualTo(Hash(1, "MvcTestingAppManifest.json")));
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  [TestCase((int)TestFailureKind.Infrastructure)]
  [TestCase((int)TestFailureKind.Timeout)]
  [TestCase((int)TestFailureKind.Canceled)]
  public void InterruptedRunPreservesCompletedCases(int failureKind)
  {
    static TestCaseDescriptor Case(string id)
    {
      var test = new TestCase(id, new Uri("executor://nunit3testexecutor"), "tests.dll");
      return new(test, id, id, id, test.ExecutorUri.ToString(), "tests.dll");
    }
    var passed = Case("passed");
    var missing = Case("missing");
    var results = TestResultClassifier.InterruptedResults([passed, missing], [new TestResult(passed.Case) { Outcome = TestOutcome.Passed }], false, null, "host failed", (TestFailureKind)failureKind);
    Assert.That(results[passed.Id].FailureKind, Is.EqualTo(TestFailureKind.None));
    Assert.That(results[missing.Id].FailureKind, Is.EqualTo((TestFailureKind)failureKind));
    Assert.That(results[missing.Id].ExecutionObserved, Is.False);
  }

  [Test]
  public void HostAssetResolutionLeavesAbsoluteArgumentsIntact()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var relative = Path.Combine("microsoft.testplatform.testhost", "18.11.1", "lib", "net8.0", "testhost.dll");
    var host = Path.Combine(directory, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(host)!);
    File.WriteAllText(host, "host");
    try
    {
      var absolute = "exec \"" + host + "\" --port 123";
      Assert.That(ProcessTestHostLauncher.ResolveTestHostArguments(absolute, [], directory), Is.EqualTo(absolute));
      Assert.That(ProcessTestHostLauncher.ResolveTestHostArguments("exec " + relative + " --port 123", [], directory), Is.EqualTo(absolute));
    }
    finally { Directory.Delete(directory, true); }
  }

  [Test]
  public void ControllerDeadlineBoundsAnUnresponsiveOperation()
  {
    using var release = new ManualResetEventSlim();
    using var finished = new ManualResetEventSlim();
    try
    {
      Assert.Throws<TimeoutException>(() => ControllerDeadline.Run(() => { release.Wait(); finished.Set(); }, TimeSpan.FromMilliseconds(30)));
    }
    finally { release.Set(); Assert.That(finished.Wait(TimeSpan.FromSeconds(5)), Is.True); }
  }

  [Test]
  public void ConditionalAccessAndMultilineQueriesCompileWithoutChangingInactiveBehavior()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    Directory.CreateDirectory(directory);
    try
    {
      var source = Path.Combine(directory, "Product.cs");
      var target = Path.Combine(directory, "rewritten.cs");
      var project = Path.Combine(directory, "Product.csproj");
      File.WriteAllText(source, """
        using System;
        using System.Linq;
        public static class Product
        {
          public static int Calls;
          static string Get(string value) { Calls++; return value; }
          public static string Trim(string value) => Get(value)?.Trim() ?? string.Empty;
          public static int Query(int[] items) => items
            .Select(value => new { Year = value, Amount = value + 1 })
            .GroupBy(item => new { item.Year })
            .Select(group => new { group.Key.Year, Sum = group.Sum(item => item.Amount) })
            .OrderBy(item => item.Year)
            .ThenBy(item => item.Sum)
            .First().Sum;
          public static int[] Lambda(int[] items) => items.Select(value => { return value + 1; }).ToArray();
          public static FormattableString Sql(int value) => $"SELECT {value}";
          public static int? Coalesce(int? value, int fallback) => value ?? fallback;
          public static int NonNullableCoalesce(int? value, int fallback) => value ?? fallback;
          public static int AssignedInBlock() { int value; { value = 42; } return value; }
          public static void AssignedBeforeCatchExit() { int value; try { value = 42; } catch { throw; } Console.WriteLine(value); }
          public static void Guard(object value) { if (value is not string text) { return; } Console.WriteLine(text); }
          public static async System.Threading.Tasks.Task AsyncThrow() { await System.Threading.Tasks.Task.Yield(); throw new Exception(); }
        }
        """);
      var options = new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [],
        Mutators = new HashSet<string>(["string-method-constants", "null-coalescing", "linq-methods", "binary-operators", "string-values", "statement-removal"])
      };
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, options, [], CancellationToken.None);
      File.Copy(source, target);
      try
      {
        MutationSchemaBuilder.RewriteSources(new()
        {
          [source] = target
        }, candidates, directory, directory);
      }
      catch
      {
        TestContext.WriteLine(File.ReadAllText(target));
        throw;
      }
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => id == Active; public static bool IsActiveAndHit(int s, int id) => IsActive(id); public static T Probe<T>(int id, System.Func<T> f) => f(); public static void Hit(int id) {} }";
      var compilation = CSharpCompilation.Create("Regression" + Guid.NewGuid().ToString("N"),
          [CSharpSyntaxTree.ParseText(File.ReadAllText(target)), CSharpSyntaxTree.ParseText(runtime)], TestWorkspace.PlatformReferences,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      using var bytes = new MemoryStream();
      var emitted = compilation.Emit(bytes);
      Assert.That(emitted.Success, Is.True, string.Join(Environment.NewLine, emitted.Diagnostics));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var product = assembly.GetType("Product")!;
      Assert.That(product.GetMethod("Trim")!.Invoke(null, [" a "]), Is.EqualTo("a"));
      Assert.That(product.GetField("Calls")!.GetValue(null), Is.EqualTo(1));
      Assert.That(product.GetMethod("Trim")!.Invoke(null, [null]), Is.EqualTo(""));
      Assert.That(product.GetMethod("Query")!.Invoke(null, [new[] { 2, 1 }]), Is.EqualTo(2));
      var mutation = candidates.Single(candidate => candidate.ReplacementToken == "Trim->constant");
      assembly.GetType("MutationRuntime")!.GetField("Active")!.SetValue(null, int.Parse(mutation.MutantRuntimeId));
      Assert.That(product.GetMethod("Trim")!.Invoke(null, [" a "]), Is.EqualTo(""));
      Assert.That(product.GetField("Calls")!.GetValue(null), Is.EqualTo(3));
    }
    finally { Directory.Delete(directory, true); }
  }

  [Test]
  public void WorkspaceDoesNotStageToolCachesAndHonorsCancellation()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var source = Path.Combine(directory, "source");
    var destination = Path.Combine(directory, "copy");
    try
    {
      foreach (var child in new[] { "src", ".dotnet", ".nuget", ".npm-cache", ".idea" })
      {
        Directory.CreateDirectory(Path.Combine(source, child));
        File.WriteAllText(Path.Combine(source, child, "file"), "data");
      }
      var copied = MutationWorkspace.CopyProject(source, destination);
      Assert.That(copied.Files, Is.EqualTo(1));
      Assert.That(File.ReadAllText(Path.Combine(destination, "src", "file")), Is.EqualTo("data"));
      Assert.Throws<OperationCanceledException>(() => MutationWorkspace.CopyProject(source, destination, new CancellationToken(true)));
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }
}
