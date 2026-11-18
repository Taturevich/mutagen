using System.Collections;
using System.Diagnostics;
using System.Text.Json;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class ReportingTests
{
  [TestCase("json", true, false)]
  [TestCase("html", false, true)]
  [TestCase("json,html", true, true)]
  [TestCase("HTML,JSON", true, true)]
  [Category("Component")]
  public async Task OnlySelectedReportsReplaceExistingFiles(string formats, bool json, bool html)
  {
    var directory = TestWorkspace.NewOutputDirectory();
    Directory.CreateDirectory(directory);
    try
    {
      var result = Result(directory);
      await File.WriteAllTextAsync(result.JsonReportPath, "previous json");
      await File.WriteAllTextAsync(result.HtmlReportPath, "previous html");
      var jsonTime = File.GetLastWriteTimeUtc(result.JsonReportPath);
      var htmlTime = File.GetLastWriteTimeUtc(result.HtmlReportPath);
      var written = await MutationReportWriter.WriteAsync(result, formats, Stopwatch.StartNew(), CancellationToken.None);
      if (json)
      {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.JsonReportPath));
        Assert.That(document.RootElement.GetProperty("TotalDurationMs").GetInt64(), Is.EqualTo(written.TotalDurationMs));
        Assert.That(document.RootElement.GetProperty("ReportSchemaVersion").GetInt32(), Is.EqualTo(4));
      }
      else
      {
        Assert.That(await File.ReadAllTextAsync(result.JsonReportPath), Is.EqualTo("previous json"));
        Assert.That(File.GetLastWriteTimeUtc(result.JsonReportPath), Is.EqualTo(jsonTime));
      }
      if (html)
      {
        Assert.That(await File.ReadAllTextAsync(result.HtmlReportPath), Does.StartWith("<!doctype html>"));
      }
      else
      {
        Assert.That(await File.ReadAllTextAsync(result.HtmlReportPath), Is.EqualTo("previous html"));
        Assert.That(File.GetLastWriteTimeUtc(result.HtmlReportPath), Is.EqualTo(htmlTime));
      }
      Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(2));
    }
    finally { Directory.Delete(directory, true); }
  }

  [TestCase("invalid")]
  [TestCase("")]
  [TestCase("json,pdf")]
  [Category("Unit")]
  public void InvalidSelectionFailsBeforePublication(string formats)
  {
    Assert.ThrowsAsync<ArgumentException>(() => MutationReportWriter.WriteAsync(Result(Path.GetTempPath()), formats,
        Stopwatch.StartNew(), CancellationToken.None));
  }

  [TestCase(false)]
  [TestCase(true)]
  [Category("Component")]
  public async Task CancellationAndSerializationFailurePreserveValidReportsAndCleanStaging(bool cancel)
  {
    var directory = TestWorkspace.NewOutputDirectory();
    Directory.CreateDirectory(directory);
    using var cancellation = new CancellationTokenSource();
    try
    {
      var result = Result(directory);
      await File.WriteAllTextAsync(result.JsonReportPath, "previous json");
      await File.WriteAllTextAsync(result.HtmlReportPath, "previous html");
      result = result with
      {
        Diagnostics = new FailingDiagnostics(() =>
      {
        if (cancel)
        {
          cancellation.Cancel();
        }
        else
        {
          throw new InvalidOperationException("serialization fixture");
        }
      })
      };
      if (cancel)
      {
        _ = Assert.CatchAsync<OperationCanceledException>(() => MutationReportWriter.WriteAsync(result, "json,html", Stopwatch.StartNew(), cancellation.Token));
      }
      else
      {
        _ = Assert.CatchAsync<InvalidOperationException>(() => MutationReportWriter.WriteAsync(result, "json,html", Stopwatch.StartNew(), cancellation.Token));
      }
      Assert.That(await File.ReadAllTextAsync(result.JsonReportPath), Is.EqualTo("previous json"));
      Assert.That(await File.ReadAllTextAsync(result.HtmlReportPath), Is.EqualTo("previous html"));
      Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(2));
    }
    finally { Directory.Delete(directory, true); }
  }

  private sealed class FailingDiagnostics(Action onRead) : IReadOnlyList<string>
  {
    public int Count => 1;
    public string this[int index] { get { onRead(); return "diagnostic"; } }
    public IEnumerator<string> GetEnumerator()
    {
      onRead();
      yield return "diagnostic";
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
  }

  private static MutationRunResult Result(string directory) => new("Completed", [], 0, 0, 0, null,
      new(0, 0, 0, 0, 0, 0, 0), 0, new Dictionary<string, IReadOnlyList<string>>(), [],
      Path.Combine(directory, "mutation-report.json"), Path.Combine(directory, "index.html"));
}
