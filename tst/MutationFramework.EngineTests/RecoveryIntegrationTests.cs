using System.Diagnostics;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class RecoveryIntegrationTests
{
  [Test]
  [Category("Unit")]
  public void SavedBaselineContextTracksResolvedAssetsAndExecutionPolicyAndRejectsUnresolvedImports()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    Directory.CreateDirectory(directory);
    try
    {
      var project = Path.Combine(directory, "Product.csproj");
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      var assetsDirectory = Path.Combine(directory, "obj");
      Directory.CreateDirectory(assetsDirectory);
      var packageRoot = Path.Combine(directory, "packages");
      var dependency = Path.Combine(packageRoot, "sample", "1.0", "lib", "net11.0", "sample.dll");
      Directory.CreateDirectory(Path.GetDirectoryName(dependency)!);
      File.WriteAllText(dependency, "first asset version");
      var packageFolders = new Dictionary<string, object> { [packageRoot + Path.DirectorySeparatorChar] = new { } };
      var libraries = new Dictionary<string, object> { ["sample/1.0"] = new { type = "package", path = "sample/1.0" } };
      var targets = new Dictionary<string, object>
      {
        ["net11.0"] = new Dictionary<string, object>
        {
          ["sample/1.0"] = new { compile = new Dictionary<string, object> { ["lib/net11.0/sample.dll"] = new { } } }
        }
      };
      File.WriteAllText(Path.Combine(assetsDirectory, "project.assets.json"), System.Text.Json.JsonSerializer.Serialize(new
      {
        packageFolders,
        libraries,
        targets
      }));
      var evaluation = new ProjectEvaluation([], CSharpParseOptions.Default, "net11.0", "Product", [], [project], new Dictionary<string, string>());
      MutationOptions Options(bool prioritize = true, int runTimeout = 1800, int startupTimeout = 60,
          IReadOnlyDictionary<string, IReadOnlyList<string>>? locks = null) => new()
          {
            ProjectPath = project,
            TestProjects = [],
            BaselineScopeKey = "stable-state",
            PrioritizeFastTests = prioritize,
            MaxRunTimeoutSeconds = runTimeout,
            HostStartupTimeoutSeconds = startupTimeout,
            ResourceLocksByTestId = locks ?? new Dictionary<string, IReadOnlyList<string>>()
          };
      string Context(MutationOptions options) => SavedBaselineStore.Context(project, null, evaluation, [], options);

      var baseline = Context(Options());
      Assert.That(Context(Options(prioritize: false)), Is.Not.EqualTo(baseline));
      Assert.That(Context(Options(runTimeout: 1801)), Is.Not.EqualTo(baseline));
      Assert.That(Context(Options(startupTimeout: 61)), Is.Not.EqualTo(baseline));
      Assert.That(Context(Options(locks: new Dictionary<string, IReadOnlyList<string>> { ["test-case"] = ["database"] })), Is.Not.EqualTo(baseline));
      File.WriteAllText(dependency, "changed asset version");
      Assert.That(Context(Options()), Is.Not.EqualTo(baseline));

      File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project><Import Project=\"*.targets\" /></Project>");
      Assert.Throws<InvalidOperationException>(() => Context(Options()));
    }
    finally
    {
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task SavedNativeBaselineReusesCompleteOutcomesAndRejectsTampering()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var product = Path.Combine(directory, "ZProduct");
    var dependency = Path.Combine(directory, "ADependency");
    Directory.CreateDirectory(product);
    Directory.CreateDirectory(dependency);
    try
    {
      var project = Path.Combine(product, "Product.csproj");
      var dependencyProject = Path.Combine(dependency, "ADependency.csproj");
      File.WriteAllText(dependencyProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      var dependencySource = Path.Combine(dependency, "Dependency.cs");
      File.WriteAllText(dependencySource, "public static class Dependency { public static int Anchor(int value) => value + 1; public static int Helper() => 0; }");
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../ADependency/ADependency.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(product, "Product.cs"), "public static class Product { public static int Compute(int value) => value + 1; }");
      var noMutantsSource = Path.Combine(product, "NoMutants.cs");
      const string originalNoMutantsSource = "public static class NoMutants { public const string Marker = \"stable\"; }";
      File.WriteAllText(noMutantsSource, originalNoMutantsSource);
      var tests = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "Tests"), project,
          "using NUnit.Framework; public sealed class Cases { [Test] public void Anchor() => Assert.That(Dependency.Anchor(1), Is.EqualTo(2)); [Test] public void Caller() => Assert.That(Product.Compute(3), Is.Not.EqualTo(Dependency.Helper())); }");
      var baseline = Path.Combine(directory, "native-baseline.json");
      MutationOptions Options(string output, string? input = null, string? save = null) => new()
      {
        ProjectPath = project,
        TestProjects = [tests],
        Mutators = new HashSet<string>(["binary-operators"]),
        OutputDirectory = output,
        CacheDirectory = Path.Combine(directory, "cache"),
        BaselinePath = input,
        SaveBaselinePath = save,
        BaselineScopeKey = "stable-test-fixtures-v1",
        MaxWorkers = 1,
        TimeoutSeconds = 30
      };
      var first = await MutationEngine.RunAsync(Options(Path.Combine(directory, "first"), save: baseline));
      Assert.That(first.OverallStatus, Is.EqualTo("Completed"));
      using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(baseline)))
      {
        var sourceKeys = saved.RootElement.GetProperty("ProductionFiles").EnumerateObject().Select(property => property.Name).ToArray();
        Assert.That(sourceKeys, Does.Contain("Product.cs"));
        Assert.That(sourceKeys, Does.Contain("../ADependency/Dependency.cs"));
      }
      var delta = await MutationEngine.RunAsync(Options(Path.Combine(directory, "delta"), baseline));
      Assert.That(delta.Mutants.Select(m => m.Status), Is.EqualTo(first.Mutants.Select(m => m.Status)));
      Assert.That(delta.Counters.MutationTestExecutions, Is.Zero);
      Assert.That(delta.Diagnostics, Has.Some.Contains("SavedBaseline: reused"));
      File.WriteAllText(noMutantsSource, originalNoMutantsSource + "\n// changed file with no selected mutants");
      var unselectedSourceChange = await MutationEngine.RunAsync(Options(Path.Combine(directory, "unselected-source"), baseline));
      Assert.That(unselectedSourceChange.Counters.MutationTestExecutions, Is.EqualTo(first.Counters.CoveredMutants));
      File.WriteAllText(noMutantsSource, originalNoMutantsSource);

      File.WriteAllText(dependencySource, "public static class Dependency { public static int Anchor(int value) => value + 1; public static int Helper() => 2; }");
      var mixedMethodChange = await MutationEngine.RunAsync(Options(Path.Combine(directory, "mixed-method-change"), baseline));
      var staleOutcomeWouldExist = first.Mutants.Join(mixedMethodChange.Mutants, prior => prior.Id, current => current.Id,
          (prior, current) => (prior, current)).Any(pair => pair.prior.SourceFile.EndsWith("Product.cs", StringComparison.Ordinal) &&
          pair.prior.Status == MutationStatus.Survived && pair.current.Status == MutationStatus.Killed);
      Assert.That(staleOutcomeWouldExist, Is.True, "Changing an unmutated helper must rerun caller mutants even when another method in that file contains selected mutants.");
      Assert.That(mixedMethodChange.Counters.MutationTestExecutions, Is.EqualTo(first.Counters.CoveredMutants));
      File.WriteAllText(baseline, File.ReadAllText(baseline).Replace("\"Engine\": \"", "\"Engine\": \"x", StringComparison.Ordinal));
      await Assert.ThrowsAsync<InvalidDataException>(async () => await MutationEngine.RunAsync(Options(Path.Combine(directory, "tampered"), baseline)));
    }
    finally
    {
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task PlanStagesSiblingHintPathAssembly()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var productDirectory = Path.Combine(directory, "Product");
    var external = Path.Combine(directory, "External");
    Directory.CreateDirectory(productDirectory);
    Directory.CreateDirectory(external);
    try
    {
      var dependency = CSharpCompilation.Create("Dependency", [CSharpSyntaxTree.ParseText("public static class Dependency { public static int Value => 41; }")],
          TestWorkspace.PlatformReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      Assert.That(dependency.Emit(Path.Combine(external, "Dependency.dll")).Success, Is.True);
      var production = Path.Combine(productDirectory, "Product.csproj");
      File.WriteAllText(production, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><Reference Include=\"Dependency\"><HintPath>../External/Dependency.dll</HintPath></Reference></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(productDirectory, "Product.cs"), "public static class Product { public static int Compute() => Dependency.Value + 1; }");
      var tests = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "Tests"), production,
          "using NUnit.Framework; public sealed class Cases { [Test] public void Case() { Assert.That(Product.Compute(), Is.EqualTo(42)); var root = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, \"External\", \"contract.txt\"))) root = root.Parent; Assert.That(root, Is.Not.Null, \"Declared repository contract inputs must be staged.\"); } }");
      File.WriteAllText(Path.Combine(external, "contract.txt"), "contract");
      File.WriteAllText(tests, File.ReadAllText(tests).Replace("</Project>", "<ItemGroup><None Include=\"../External/contract.txt\" /></ItemGroup></Project>", StringComparison.Ordinal));
      var result = await MutationEngine.PlanAsync(new MutationOptions
      {
        ProjectPath = production,
        TestProjects = [tests],
        Mutators = new HashSet<string>(["binary-operators"]),
        OutputDirectory = Path.Combine(directory, "reports"),
        MaxWorkers = 1
      });
      Assert.That(result.OverallStatus, Is.EqualTo("Planned"));
      Assert.That(result.Counters.CoveredMutants, Is.EqualTo(1));
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task FastAssemblyKillsBeforeSlowerAssemblyIsLaunchedForTheMutant()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var productDirectory = Path.Combine(directory, "Product");
    Directory.CreateDirectory(productDirectory);
    try
    {
      var production = Path.Combine(productDirectory, "Product.csproj");
      File.WriteAllText(production, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productDirectory, "Product.cs"), "public static class Product { public static int Compute(int a) => a + 1; }");
      var marker = Path.Combine(directory, "slow-mutant-started");
      var slow = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "AIntegration"), production,
          "using NUnit.Framework; public sealed class SlowTests { [Test] public void Case() { System.Threading.Thread.Sleep(100); if (System.Environment.GetEnvironmentVariable(\"MUTATION_ACTIVE_ID\") != null) System.IO.File.WriteAllText(@\"" + marker + "\", \"started\"); Assert.That(Product.Compute(1), Is.EqualTo(2)); } }");
      var fast = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "ZUnit"), production,
          "using NUnit.Framework; public sealed class FastTests { [Test] public void Case() => Assert.That(Product.Compute(1), Is.EqualTo(2)); }");
      var result = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = production,
        TestProjects = [slow, fast],
        Mutators = new HashSet<string>(["binary-operators"]),
        OutputDirectory = Path.Combine(directory, "reports"),
        MaxWorkers = 1,
        TimeoutSeconds = 30
      });
      Assert.That(result.KilledCount, Is.EqualTo(1));
      Assert.That(result.Counters.BaselineTestExecutions, Is.EqualTo(2));
      Assert.That(result.Counters.MutationTestExecutions, Is.EqualTo(1));
      Assert.That(File.Exists(marker), Is.False);
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  [TestCase(false)]
  [TestCase(true)]
  [Category("EndToEnd")]
  public async Task CheckpointResumesDefaultPerMutantExecutionAfterFreshBaseline(bool webApplication)
  {
    var directory = TestWorkspace.NewOutputDirectory();
    Directory.CreateDirectory(directory);
    try
    {
      var productDirectory = Path.Combine(directory, "Product");
      Directory.CreateDirectory(productDirectory);
      var production = Path.Combine(productDirectory, "Product.csproj");
      File.WriteAllText(production, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productDirectory, "Product.cs"), "public static class Product { public static int Compute(int a) => a + 1; }");
      if (webApplication)
      {
        File.WriteAllText(Path.Combine(productDirectory, "Product.cs"), "public class Product { public static int Compute(int a) => a + 1; }");
        File.WriteAllText(production, "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><StaticWebAssetsEnabled>false</StaticWebAssetsEnabled></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(productDirectory, "Program.cs"), "using Microsoft.AspNetCore.Builder; var app = WebApplication.CreateBuilder(args).Build(); app.MapGet(\"/value\", () => Product.Compute(1).ToString()); app.Run();");
      }
      var tests = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "Tests"), production,
          "using NUnit.Framework; public sealed class Tests { [Test] public void Case() => Assert.That(Product.Compute(1), Is.EqualTo(2)); }");
      if (webApplication)
      {
        File.WriteAllText(tests, File.ReadAllText(tests).Replace("</Project>", "<ItemGroup><PackageReference Include=\"Microsoft.AspNetCore.Mvc.Testing\" Version=\"11.0.0\" /></ItemGroup></Project>", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(tests)!, "Tests.cs"), "using NUnit.Framework; public sealed class Tests { [Test] public async System.Threading.Tasks.Task Case() { using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Product>(); using var client = factory.CreateClient(); Assert.That(await client.GetStringAsync(\"/value\"), Is.EqualTo(\"2\")); } }");
      }
      MutationOptions Options(string scope) => new()
      {
        ProjectPath = production,
        TestProjects = [tests],
        Mutators = new HashSet<string>(["binary-operators"]),
        OutputDirectory = Path.Combine(directory, "reports"),
        CacheDirectory = Path.Combine(directory, "cache"),
        CheckpointDirectory = Path.Combine(directory, "checkpoint"),
        CheckpointScopeKey = scope,
        MaxWorkers = 1,
        TimeoutSeconds = 30
      };
      var first = await MutationEngine.RunAsync(Options("same-state"));
      Assert.That(first.KilledCount, Is.EqualTo(1));
      var resumed = await MutationEngine.RunAsync(Options("same-state"));
      Assert.That(resumed.KilledCount, Is.EqualTo(1));
      Assert.That(resumed.Counters.BaselineTestExecutions, Is.EqualTo(1));
      if (resumed.Counters.MutationTestExecutions != 0)
      {
        foreach (var manifest in Directory.EnumerateFiles(Path.Combine(directory, "checkpoint/run-manifest"), "*.json"))
        {
          TestContext.Out.WriteLine(File.ReadAllText(manifest));
        }
      }
      Assert.That(resumed.Counters.MutationTestExecutions, Is.Zero);
      var invalidated = await MutationEngine.RunAsync(Options("changed-state"));
      Assert.That(invalidated.Counters.MutationTestExecutions, Is.EqualTo(1));
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task TwoTestAssembliesWithDifferentTestSdkVersionsCollectBaselineCoverage()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var productDirectory = Path.Combine(directory, "Product");
    Directory.CreateDirectory(productDirectory);
    try
    {
      var production = Path.Combine(productDirectory, "Product.csproj");
      File.WriteAllText(production, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productDirectory, "Product.cs"), "public static class Product { public static int Compute(int a) => a + 1; }");
      var first = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "First"), production, "using NUnit.Framework; public sealed class FirstTests { [Test] public void Case() => Assert.That(Product.Compute(1), Is.EqualTo(2)); }");
      var second = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "Second"), production, "using NUnit.Framework; public sealed class SecondTests { [Test] public void Case() => Assert.That(Product.Compute(2), Is.EqualTo(3)); }");
      File.WriteAllText(first, File.ReadAllText(first).Replace("18.12.0", "18.11.1", StringComparison.Ordinal));
      var result = await MutationEngine.PlanAsync(new MutationOptions
      {
        ProjectPath = production,
        TestProjects = [first, second],
        Mutators = new HashSet<string>(["binary-operators"]),
        OutputDirectory = Path.Combine(directory, "reports"),
        MaxWorkers = 1,
        TimeoutSeconds = 30
      });
      Assert.That(result.OverallStatus, Is.EqualTo("Planned"));
      Assert.That(result.Tests, Has.Count.EqualTo(2));
      Assert.That(result.CoverageStatuses.Values.All(status => status == CoverageStatus.CoverageSuccess), Is.True);
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task HangingCasePreservesPriorSuccessAndTerminatesOwnedProcesses()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var productDirectory = Path.Combine(directory, "Product");
    Directory.CreateDirectory(productDirectory);
    try
    {
      var production = Path.Combine(productDirectory, "Product.csproj");
      File.WriteAllText(production, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productDirectory, "Product.cs"), "public static class Product { public static int Compute(int a) => a + 1; }");
      var marker = Path.Combine(directory, "host.pid");
      var code = "using NUnit.Framework; public sealed class Cases { [Test, Order(1)] public void Fast() {} [Test, Order(2)] public void Hang() { System.IO.File.WriteAllText(@\"" + marker + "\", System.Environment.ProcessId.ToString()); System.Threading.Thread.Sleep(120000); } }";
      var tests = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "Tests"), production, code);
      var discovered = await MutationEngine.DiscoverAsync(new MutationOptions { ProjectPath = production, TestProjects = [tests], OutputDirectory = directory });
      var source = discovered.Tests[0].SourceAssembly;
      var logs = Path.Combine(directory, "host-logs");
      using var session = new TestPlatformSession(TestPlatformPaths.FindConsole(tests), () => { }, logs);
      var selected = session.Discover([source], CancellationToken.None);
      var results = await Task.Run(() => session.ExecuteMany(selected, null, null, 5, () => { }, CancellationToken.None,
          serializeTests: true, testTimeoutSeconds: 1)).WaitAsync(TimeSpan.FromSeconds(15));
      var fast = selected.Single(test => test.DisplayName == "Fast");
      var hang = selected.Single(test => test.DisplayName == "Hang");
      Assert.That(results[fast.Id].FailureKind, Is.EqualTo(TestFailureKind.None));
      Assert.That(results[hang.Id].FailureKind, Is.EqualTo(TestFailureKind.Timeout));
      Assert.That(File.Exists(marker), Is.True);
      var hostId = int.Parse(File.ReadAllText(marker));
      session.Dispose();
      for (var attempt = 0; attempt < 50 && Alive(hostId); attempt++)
      {
        await Task.Delay(100);
      }
      Assert.That(Alive(hostId), Is.False);
      Assert.That(Directory.GetFiles(logs, "*.controller"), Is.Empty);
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  private static bool Alive(int id)
  {
    try
    {
      using var process = Process.GetProcessById(id);
      return !process.HasExited;
    }
    catch (ArgumentException) { return false; }
  }
}
