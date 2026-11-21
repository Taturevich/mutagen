using System.Collections.Concurrent;
using System.Diagnostics;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class MutationProgressTests
{
  [TestCase(-1), TestCase(5_000_000), Category("Unit")]
  public void InvalidIntervalsAreRejectedBeforeProjectEvaluation(int interval)
  {
    Assert.That(() => MutationOptionsValidator.ValidateForRun(new MutationOptions
    {
      ProjectPath = "unused.csproj",
      TestProjects = [],
      ProgressIntervalSeconds = interval
    }), Throws.ArgumentException.With.Message.Contains("Progress interval"));
  }

  private static MutationResult Result(string id, MutationStatus status = MutationStatus.Killed) =>
      new(id, "site", "Arithmetic", "Product.cs", 1, 1, "a+1", "a-1", status, [], []);

  [Test, Category("Unit")]
  public async Task ReusedResultsDoNotInflateThroughputOrEta()
  {
    await using var progress = new MutationProgressReporter(110, 2,
        Enumerable.Range(0, 100).Select(index => Result(index.ToString())), TimeSpan.Zero);
    progress.Record(Result("first", MutationStatus.Survived));
    Assert.That(progress.Snapshot(TimeSpan.FromSeconds(60)), Does.Contain("ETA warming up"));
    for (var index = 0; index < 4; index++)
    {
      progress.Record(Result("new" + index, MutationStatus.Survived));
    }
    Assert.That(progress.Snapshot(TimeSpan.FromSeconds(60)), Does.Contain("105/110").And.Contain("remaining 5")
        .And.Contain("killed 100").And.Contain("survived 5").And.Contain("reused 100")
        .And.Contain("rate 5.0 mutants/min").And.Contain("ETA ~00:01:00"));
    Assert.That(progress.Snapshot(TimeSpan.FromSeconds(2)), Does.Contain("ETA warming up"));
  }

  [Test, Category("Unit")]
  public async Task ConcurrentCompletionsAndWorkLeasesKeepAccurateCounts()
  {
    await using var progress = new MutationProgressReporter(200, 4, [], TimeSpan.Zero);
    using (progress.StartWork())
    {
      Assert.That(progress.Snapshot(TimeSpan.Zero), Does.Contain("active workers 1/4"));
      await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(() => progress.Record(Result(index.ToString(), MutationStatus.Timeout)))));
    }
    Assert.That(progress.Snapshot(TimeSpan.FromSeconds(20)), Does.Contain("200/200").And.Contain("timeout 200")
        .And.Contain("active workers 0/4").And.Contain("ETA 00:00:00"));
  }

  [Test, Category("Unit")]
  public async Task HeartbeatContinuesWithoutCompletionsAndDisposesWithFinalState()
  {
    var messages = new ConcurrentQueue<string>();
    var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using (var progress = new MutationProgressReporter(10, 1, [], TimeSpan.FromMilliseconds(20), message =>
    {
      messages.Enqueue(message);
      if (message.Contains("(running)", StringComparison.Ordinal))
      {
        tick.TrySetResult();
      }
    }))
    {
      await tick.Task.WaitAsync(TimeSpan.FromSeconds(5));
      progress.Complete("canceled");
    }
    Assert.That(messages.Last(), Does.Contain("(canceled) 0/10").And.Contain("remaining 10"));
    Assert.That(messages.Count(message => message.Contains("(canceled)", StringComparison.Ordinal)), Is.EqualTo(1));
  }

  [Test, Category("Unit")]
  public async Task DisabledAndClosedDiagnosticStreamsDoNotInterruptExecution()
  {
    await using (var disabled = new MutationProgressReporter(0, 0, [], TimeSpan.Zero, _ => Assert.Fail("Progress disabled")))
    {
      Assert.That(disabled.Snapshot(TimeSpan.Zero), Does.Contain("0/0 (100.0%)"));
    }
    await using var closed = new MutationProgressReporter(1, 1, [], TimeSpan.FromHours(1), _ => throw new IOException("closed"));
    closed.Record(Result("one"));
    closed.Complete("finished");
  }

  [Test, Category("EndToEnd")]
  public async Task CliReportsRunningAndFinalProgressOnStderr()
  {
    var directory = TestWorkspace.NewOutputDirectory();
    var productDirectory = Path.Combine(directory, "Product");
    Directory.CreateDirectory(productDirectory);
    using var process = new Process();
    var started = false;
    try
    {
      var project = Path.Combine(productDirectory, "Product.csproj");
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productDirectory, "Product.cs"), "public static class Product { public static int Compute(int value) => value + 1; }");
      var tests = TestWorkspace.WriteNUnitProject(Path.Combine(directory, "Tests"), project,
          "using NUnit.Framework; public sealed class Cases { [Test] public void Case() { System.Threading.Thread.Sleep(1500); Assert.That(Product.Compute(1), Is.EqualTo(2)); } }");
      process.StartInfo = new ProcessStartInfo("dotnet")
      {
        WorkingDirectory = directory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      };
      foreach (var argument in new[] { typeof(MutationEngine).Assembly.Location, "run", "--project", project, "--tests", tests,
        "--mutators", "binary-operators", "--workers", "1", "--timeout", "30", "--progress-interval", "1",
        "--output", Path.Combine(directory, "reports"), "--report", "json" })
      {
        process.StartInfo.ArgumentList.Add(argument);
      }
      started = process.Start();
      Assert.That(started, Is.True);
      var stdout = process.StandardOutput.ReadToEndAsync();
      var stderr = process.StandardError.ReadToEndAsync();
      await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
      var diagnostics = await stderr;
      Assert.That(process.ExitCode, Is.Zero, diagnostics);
      Assert.That(diagnostics, Does.Contain("progress (running)").And.Contain("active workers 1/1")
          .And.Contain("progress (finished) 1/1").And.Contain("killed 1"));
      Assert.That(await stdout, Does.Contain("killed: 1").And.Not.Contain("Mutagen: progress"));
    }
    finally
    {
      if (started && !process.HasExited)
      {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
      }
      Directory.Delete(directory, true);
    }
  }
}
