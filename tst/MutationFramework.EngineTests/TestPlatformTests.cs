using MutationFramework.Sdk;
using NUnit.Framework;
using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class TestPlatformTests
{
  [Test]
  [Category("EndToEnd")]
  public async Task MutationTimeoutTerminatesBusyTestHostAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenTimeoutTermination", Guid.NewGuid().ToString("N"));
    var existingTestHostIds = GetTestHostProcessIds();
    Directory.CreateDirectory(root);
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
          "using NUnit.Framework; [TestFixture] public sealed class TimeoutFixtureTests { [Test] public void MutationTest() { if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(\"MUTATION_ACTIVE_ID\"))) { while (true) { System.Threading.Thread.SpinWait(10000); } } Assert.That(TimeoutFixture.Compute(), Is.EqualTo(3)); } }");

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
      });
      if (await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(30))) != runTask)
      {
        TerminateNewTestHosts(existingTestHostIds);
        try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        Assert.Fail("Mutation execution did not return after the configured test timeout.");
      }

      var result = await runTask;
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
          try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
      }
    }
  }

  [Test]
  [Category("Unit")]
  public void FailureAttributionRequiresEvidenceFromTheSelectedTestBody()
  {
    const string testName = "Sample.Tests.CalculatorTests.Add_ReturnsCorrectSum";

    Assert.That(TestResultClassifier.HasTestBodyFailureEvidence(testName,
        "at NUnit.Framework.Assert.That(...)\r\nat Sample.Tests.CalculatorTests.Add_ReturnsCorrectSum()"), Is.True);
    Assert.That(TestResultClassifier.HasTestBodyFailureEvidence(testName + "(18,True)",
        "at Sample.Tests.CalculatorTests.Add_ReturnsCorrectSum()"), Is.True);
    Assert.That(TestResultClassifier.HasTestBodyFailureEvidence(testName,
        "at Sample.Tests.CalculatorTests.SetUp()"), Is.False);
    Assert.That(TestResultClassifier.HasTestBodyFailureEvidence(testName, null), Is.False);
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
