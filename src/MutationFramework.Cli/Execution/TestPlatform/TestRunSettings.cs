using System.Xml.Linq;

namespace MutationFramework.Sdk;

internal static class TestRunSettings
{
  internal static string RunSettings(string? probePath, string? activeMutationId, bool serializeTests = false,
      bool capturePerTestCoverage = false)
  {
    var variables = new XElement("EnvironmentVariables");
    if (probePath is not null)
    {
      variables.Add(new XElement("MUTATION_PROBE_FILE", probePath));
    }

    if (activeMutationId != null)
    {
      variables.Add(new XElement("MUTATION_ACTIVE_ID", activeMutationId));
    }

    var runConfiguration = new XElement("RunConfiguration", variables);
    var sections = new List<object> { runConfiguration };
    if (serializeTests)
    {
      runConfiguration.AddFirst(new XElement("MaxCpuCount", 1));
      runConfiguration.Add(new XElement("DisableParallelization", true));
      runConfiguration.Add(new XElement("CollectDataForEachTestSeparately", true));
      sections.Add(new XElement("NUnit", new XElement("NumberOfTestWorkers", 0)));
    }

    if (capturePerTestCoverage)
    {
      var collectorType = typeof(MutationFramework.CoverageCollector.MutationCoverageCollector);
      sections.Add(new XElement("InProcDataCollectionRunSettings",
          new XElement("InProcDataCollectors",
              new XElement("InProcDataCollector",
                  new XAttribute("friendlyName", MutationFramework.CoverageCollector.MutationCoverageCollector.FriendlyName),
                  new XAttribute("uri", MutationFramework.CoverageCollector.MutationCoverageCollector.TypeUri),
                  new XAttribute("codebase", collectorType.Assembly.Location),
                  new XAttribute("assemblyQualifiedName", collectorType.AssemblyQualifiedName!),
                  new XElement("Configuration")))));
    }

    return new XDocument(new XElement("RunSettings", sections)).ToString(SaveOptions.DisableFormatting);
  }

}
