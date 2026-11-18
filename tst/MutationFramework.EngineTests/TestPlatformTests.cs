using System.Diagnostics;

using Microsoft.CodeAnalysis;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class TestPlatformTests
{
  [Test]
  [Category("EndToEnd")]
  public async Task ExternalCancellationStopsAnActiveMutationWithoutPublishingSurvival()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var production = Path.Combine(directory, "Production");
    Directory.CreateDirectory(production);
    var marker = Path.Combine(directory, "active.pid");
    using var cancellation = new CancellationTokenSource();
    try
    {
      var project = Path.Combine(production, "Product.csproj");
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(production, "Product.cs"), "public static class Product { public static int Compute() => 1 + 2; }");
      var escapedMarker = marker.Replace("\\", "\\\\");
      var tests = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "Tests"), project,
          "using NUnit.Framework; public sealed class Tests { [Test] public void Case() { if (Product.Compute() != 3) { System.IO.File.WriteAllText(\"" + escapedMarker + "\", System.Environment.ProcessId.ToString()); System.Threading.Thread.Sleep(120000); } } }");
      var run = MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [tests],
        Mutators = new HashSet<string>(["binary-operators"]),
        MaxWorkers = 1,
        TimeoutSeconds = 30,
        OutputDirectory = Path.Combine(directory, "reports")
      }, cancellation.Token);
      for (var attempt = 0; attempt < 600 && !File.Exists(marker) && !run.IsCompleted; attempt++)
      {
        await Task.Delay(100);
      }
      Assert.That(File.Exists(marker), Is.True, "The fixture must reach active mutation execution before cancellation.");
      cancellation.Cancel();
      _ = await Assert.ThrowsAsync<OperationCanceledException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(10)));
      Assert.That(File.Exists(Path.Combine(directory, "reports", "mutation-report.json")), Is.False);
    }
    finally
    {
      cancellation.Cancel();
      if (File.Exists(marker))
      {
        try
        {
          using var host = Process.GetProcessById(int.Parse(File.ReadAllText(marker)));
          if (!host.HasExited)
          {
            host.Kill(entireProcessTree: true);
            host.WaitForExit(3000);
          }
        }
        catch (ArgumentException) { }
      }
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, true);
      }
    }
  }

  [TestCase(TestHostIsolationPolicy.PerTest)]
  [TestCase(TestHostIsolationPolicy.PerMutant)]
  [TestCase(TestHostIsolationPolicy.PerBatch)]
  [Category("EndToEnd")]
  public async Task StructuredFailuresFromHelpersAsyncEmptyStacksAndFixturesAreAttributed(TestHostIsolationPolicy policy)
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var production = Path.Combine(directory, "Production");
    Directory.CreateDirectory(production);
    try
    {
      var project = Path.Combine(production, "Product.csproj");
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(production, "Product.cs"), "public static class Product { public static int Compute(int value) => value + 1; }");
      var tests = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "Tests"), project, """
          using NUnit.Framework;
          using System;
          using System.Threading.Tasks;
          public sealed class NoStackException : Exception { public override string StackTrace => ""; }
          public sealed class BodyTests
          {
            static void Helper() { if (Product.Compute(1) != 2) throw new InvalidOperationException("helper"); }
            [Test] public void Assertion() => Assert.That(Product.Compute(1), Is.EqualTo(2));
            [Test] public void ApplicationException() { if (Product.Compute(1) != 2) throw new ArgumentException("application"); }
            [Test] public void FromHelper() => Helper();
            [Test] public async Task AsyncException() { await Task.Yield(); Helper(); }
            [Test] public void EmptyStack() { if (Product.Compute(1) != 2) throw new NoStackException(); }
            [TestCase(1)] [TestCase(2)] public void Parameterized(int value) => Assert.That(Product.Compute(value), Is.EqualTo(value + 1));
          }
          public sealed class SetupTests
          {
            [SetUp] public void Setup() { if (Product.Compute(1) != 2) throw new InvalidOperationException("setup"); }
            [Test] public void Case() { }
          }
          public sealed class TeardownTests
          {
            [Test] public void Case() { }
            [TearDown] public void Teardown() { if (Product.Compute(1) != 2) throw new InvalidOperationException("teardown"); }
          }
          """);
      // Select one case at a time so each adapter lifecycle is independently verified.
      foreach (var filter in new[] { "Assertion", "ApplicationException", "FromHelper", "AsyncException", "EmptyStack", "Parameterized", "SetupTests", "TeardownTests" })
      {
        var result = await MutationEngine.RunAsync(new MutationOptions
        {
          ProjectPath = project,
          TestProjects = [tests],
          TestFilter = filter,
          Mutators = new HashSet<string>(["binary-operators"]),
          MaxWorkers = 1,
          ExecutionMode = policy == TestHostIsolationPolicy.PerBatch ? MutationExecutionMode.Batched : MutationExecutionMode.Isolated,
          TestHostIsolationPolicy = policy,
          OutputDirectory = Path.Combine(directory, "reports"),
          TimeoutSeconds = 60,
          CacheDirectory = Path.Combine(directory, "cache")
        });
        Assert.That(result.Mutants, Has.Count.EqualTo(1), filter);
        Assert.That(result.Mutants[0].Status, Is.EqualTo(MutationStatus.Killed), filter + ": " + result.Mutants[0].Diagnostic);
        Assert.That(result.Mutants[0].KillingTests, Is.Not.Empty, filter);
      }
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  [TestCase(TestHostIsolationPolicy.PerTest, false)]
  [TestCase(TestHostIsolationPolicy.PerMutant, false)]
  [TestCase(TestHostIsolationPolicy.PerBatch, false)]
  [TestCase(TestHostIsolationPolicy.PerTest, true)]
  [TestCase(TestHostIsolationPolicy.PerMutant, true)]
  [TestCase(TestHostIsolationPolicy.PerBatch, true)]
  [Category("EndToEnd")]
  public async Task ARealTesthostCrashOrMissingResultCannotBecomeAKill(TestHostIsolationPolicy policy, bool exitWithoutResult)
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var production = Path.Combine(directory, "Production");
    Directory.CreateDirectory(production);
    try
    {
      var project = Path.Combine(production, "Product.csproj");
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(production, "Product.cs"), "public static class Product { public static int Compute() => 1 + 2; }");
      var termination = exitWithoutResult ? "System.Environment.Exit(0);" : "System.Environment.FailFast(\"mutant crash fixture\");";
      var tests = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "Tests"), project,
          "using NUnit.Framework; public sealed class Tests { [Test] public void Crash() { var result = Product.Compute(); if (result != 3) { " + termination + " } } }");
      var result = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [tests],
        Mutators = new HashSet<string>(["binary-operators"]),
        TestHostIsolationPolicy = policy,
        ExecutionMode = policy == TestHostIsolationPolicy.PerBatch ? MutationExecutionMode.Batched : MutationExecutionMode.Isolated,
        MaxWorkers = 1,
        OutputDirectory = Path.Combine(directory, "reports"),
        TimeoutSeconds = 30
      });
      Assert.That(result.Mutants.Single().Status, Is.EqualTo(MutationStatus.InfrastructureError));
      Assert.That(result.Mutants.Single().KillingTests, Is.Empty);
    }
    finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task MutationTimeoutTerminatesBusyTestHostAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenTimeoutTermination", Guid.NewGuid().ToString("N"));
    var existingTestHostIds = GetTestHostProcessIds();
    Directory.CreateDirectory(root);
    using var watchdogCancellation = new CancellationTokenSource();
    var activeMarker = Path.Combine(root, "active-execution.txt");
    try
    {
      var productionDirectory = Path.Combine(root, "Production");
      var testsDirectory = Path.Combine(root, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      var productionProject = Path.Combine(productionDirectory, "TimeoutFixture.csproj");
      var testProject = Path.Combine(testsDirectory, "TimeoutFixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "TimeoutFixture.cs"),
          "public static class TimeoutFixture { public static int Compute() => 1 + 2; }");
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\TimeoutFixture.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "TimeoutFixtureTests.cs"),
          "using NUnit.Framework; [TestFixture] public sealed class TimeoutFixtureTests { [Test] public void MutationTest() { if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(\"MUTATION_ACTIVE_ID\"))) { System.IO.File.WriteAllText(" + System.Text.Json.JsonSerializer.Serialize(activeMarker) + ", \"active\"); while (true) { System.Threading.Thread.SpinWait(10000); } } Assert.That(TimeoutFixture.Compute(), Is.EqualTo(3)); } }");

      var runTask = MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
        MaxWorkers = 1,
        TimeoutSeconds = 8,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(root, "mutation-results")
      }, watchdogCancellation.Token);
      // Project evaluation, original semantic compilation, restore and baseline discovery
      // are setup, not mutation execution. Start the watchdog only once the busy host runs.
      for (var attempt = 0; attempt < 600 && !File.Exists(activeMarker) && !runTask.IsCompleted; attempt++)
      {
        await Task.Delay(100);
      }
      if (!File.Exists(activeMarker))
      {
        if (runTask.IsCompleted)
        {
          await runTask;
        }
        Assert.Fail("The timeout fixture never entered active mutation execution.");
      }
      var mutationTimer = Stopwatch.StartNew();
      if (await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(30))) != runTask)
      {
        watchdogCancellation.Cancel();
        TerminateNewTestHosts(existingTestHostIds);
        try
        {
          await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { }
        Assert.Fail("Mutation execution did not return after the configured test timeout.");
      }

      var result = await runTask;
      Assert.That(mutationTimer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)), "The configured 8-second execution deadline plus host cleanup must remain bounded.");
      Assert.That(result.OverallStatus, Is.EqualTo("CompletedWithErrors"), string.Join(Environment.NewLine, result.Diagnostics));
      Assert.That(result.Mutants, Has.Length.EqualTo(1));
      Assert.That(result.Mutants[0].Status, Is.EqualTo(MutationStatus.Timeout), result.Mutants[0].Diagnostic);

      for (var attempt = 0; attempt < 30 && GetTestHostProcessIds().Except(existingTestHostIds).Any(); attempt++)
      {
        await Task.Delay(100);
      }
      Assert.That(GetTestHostProcessIds().Except(existingTestHostIds), Is.Empty,
          "The timed-out testhost process must be terminated before the worker continues.");
    }
    finally
    {
      watchdogCancellation.Cancel();
      TerminateNewTestHosts(existingTestHostIds);
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }

    static HashSet<int> GetTestHostProcessIds() => Process.GetProcesses()
        .Where(process => process.ProcessName.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        .Select(process =>
        {
          var id = process.Id;
          process.Dispose();
          return id;
        }).ToHashSet();

    static void TerminateNewTestHosts(IReadOnlySet<int> existingIds)
    {
      foreach (var process in Process.GetProcesses().Where(process =>
          process.ProcessName.Contains("testhost", StringComparison.OrdinalIgnoreCase) && !existingIds.Contains(process.Id)))
      {
        using (process)
        {
          try
          {
            process.Kill(entireProcessTree: true);
          }
          catch (InvalidOperationException) { }
        }
      }
    }
  }

  [Test]
  [Category("Unit")]
  public void FailureAttributionUsesExactIdentityAndExecutedActiveSiteWithoutStackTraces()
  {
    var testCase = new Microsoft.VisualStudio.TestPlatform.ObjectModel.TestCase("Fixture.Test", new Uri("executor://nunit3testexecutor/"), "Fixture.dll");
    var descriptor = new TestCaseDescriptor(testCase, "test", "Fixture.Test", "test", testCase.ExecutorUri.ToString(), testCase.Source);
    var result = new Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult(testCase)
    {
      Outcome = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestOutcome.Failed,
      ErrorMessage = "arbitrary application exception",
      ErrorStackTrace = null
    };
    var coverage = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestProperty.Register(
        MutationFramework.CoverageCollector.MutationCoverageCollector.CoverageProperty, "coverage", typeof(string), typeof(TestPlatformTests));
    Assert.That(TestResultClassifier.ConvertResult(descriptor, result, false, "7").FailureKind, Is.EqualTo(TestFailureKind.Unattributed));
    result.SetPropertyValue(coverage, "7");
    Assert.That(TestResultClassifier.ConvertResult(descriptor, result, true, "7").FailureKind, Is.EqualTo(TestFailureKind.TestBody));
    Assert.That(TestResultClassifier.ConvertResult(descriptor, result, true, "8").FailureKind, Is.EqualTo(TestFailureKind.Unattributed));
  }

  [Test]
  [Category("Unit")]
  public void SharedBatchRunSettingsDisableFrameworkParallelism()
  {
    var settings = System.Xml.Linq.XDocument.Parse(TestRunSettings.RunSettings("probe.jsonl", "1,2",
        serializeTests: true, capturePerTestCoverage: true));
    var settingsWithoutCoverageCollector = System.Xml.Linq.XDocument.Parse(
        TestRunSettings.RunSettings("probe.jsonl", "1,2", serializeTests: true));
    var root = settings.Root!;
    Assert.That(settingsWithoutCoverageCollector.Root!.Element("InProcDataCollectionRunSettings"), Is.Null);

    Assert.That(root.Element("RunConfiguration")!.Element("MaxCpuCount")!.Value, Is.EqualTo("1"));
    Assert.That(root.Element("RunConfiguration")!.Element("DisableParallelization")!.Value, Is.EqualTo("true"));
    Assert.That(root.Element("NUnit")!.Element("NumberOfTestWorkers")!.Value, Is.EqualTo("0"));
    Assert.That(root.Element("RunConfiguration")!.Element("CollectDataForEachTestSeparately")!.Value, Is.EqualTo("true"));
    Assert.That(root.Element("InProcDataCollectionRunSettings")!.Element("InProcDataCollectors")!
        .Element("InProcDataCollector")!.Attribute("friendlyName")!.Value, Is.EqualTo("MutagenPerTestCoverage"));
    Assert.That(root.Element("InProcDataCollectionRunSettings")!.Element("InProcDataCollectors")!
        .Element("InProcDataCollector")!.Attribute("codebase")!.Value, Does.EndWith("MutationFramework.CoverageCollector.dll"));
    Assert.That(root.Element("RunConfiguration")!.Element("EnvironmentVariables")!.Element("MUTATION_ACTIVE_ID")!.Value, Is.EqualTo("1,2"));
    Assert.That(ExecutionPolicy.SupportsSerializedRunSettings("executor://nunit3testexecutor/"), Is.True);
    Assert.That(ExecutionPolicy.SupportsSerializedRunSettings("executor://xunit/VsTestRunner2/netcoreapp"), Is.False);
    Assert.That(ExecutionPolicy.SupportsSerializedRunSettings("executor://mstestadapter/v2"), Is.False);
    Assert.That(ExecutionPolicy.SupportsSerializedRunSettings("executor://unknown"), Is.False);
    Assert.That(ExecutionPolicy.SupportsSerializedRunSettings("executor://nunit3testexecutor-custom/"), Is.False);
    Assert.That(ExecutionPolicy.SupportsSerializedRunSettings("executor://mstestadapter-lookalike/"), Is.False);
  }
}
