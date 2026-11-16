using System.Reflection;
using System.Xml.Linq;
using MutationFramework.CoverageCollector;
using MutationFramework.Sdk;
using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class ArchitectureBoundaryTests
{
  [Test]
  public void MutatorsAndCoverageCollectorRemainIndependentOfEngineOrchestration()
  {
    var repositoryRoot = FindRepositoryRoot();
    var mutatorProject = Path.Combine(repositoryRoot, "src", "MutationFramework.Mutators", "MutationFramework.Mutators.csproj");
    var collectorProject = Path.Combine(repositoryRoot, "src", "MutationFramework.CoverageCollector", "MutationFramework.CoverageCollector.csproj");
    var engineProject = Path.Combine(repositoryRoot, "src", "MutationFramework.Cli", "MutationFramework.Cli.csproj");

    var mutatorDocument = XDocument.Load(mutatorProject);
    var collectorDocument = XDocument.Load(collectorProject);
    var engineDocument = XDocument.Load(engineProject);
    var mutatorProjectReferences = ProjectReferences(mutatorDocument);
    var collectorProjectReferences = ProjectReferences(collectorDocument);
    var engineProjectReferences = ProjectReferences(engineDocument);

    Assert.That(mutatorProjectReferences, Is.Empty, "Mutators must not depend on the CLI, planner, runtime, reporters, or host implementations.");
    Assert.That(collectorProjectReferences, Is.Empty, "The collector must expose VSTest coverage data without depending on the engine.");
    Assert.That(engineProjectReferences, Does.Contain("MutationFramework.Mutators"));
    Assert.That(engineProjectReferences, Does.Contain("MutationFramework.CoverageCollector"));

    var mutatorReferences = ReferencedAssemblyNames(typeof(IMutator).Assembly);
    var collectorReferences = ReferencedAssemblyNames(typeof(MutationCoverageCollector).Assembly);
    var engineReferences = ReferencedAssemblyNames(typeof(MutationEngine).Assembly);
    var engineOnlyDependencies = new[] { "MutationFramework.Cli", "MutationFramework.CoverageCollector", "Microsoft.TestPlatform.TranslationLayer" };
    var hostDependencies = new[] { "MutationFramework.Cli", "MutationFramework.Mutators", "Microsoft.TestPlatform.TranslationLayer" };

    Assert.That(mutatorReferences, Does.Contain("Microsoft.CodeAnalysis.CSharp"));
    Assert.That(mutatorReferences.Intersect(engineOnlyDependencies, StringComparer.Ordinal), Is.Empty,
        "The mutator contract/implementations must not reference engine, runtime, or VSTest orchestration assemblies.");
    Assert.That(collectorReferences, Does.Contain("Microsoft.VisualStudio.TestPlatform.ObjectModel"));
    Assert.That(collectorReferences.Intersect(hostDependencies, StringComparer.Ordinal), Is.Empty,
        "The in-process collector must not depend on mutators or engine orchestration.");
    Assert.That(engineReferences, Does.Contain("MutationFramework.Mutators"));
    Assert.That(engineReferences, Does.Contain("MutationFramework.CoverageCollector"));

    var mutatorSource = Directory.GetFiles(Path.GetDirectoryName(mutatorProject)!, "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .SelectMany(File.ReadAllLines);
    var forbiddenMutatorReferences = new[]
    {
      "MutationFramework.Cli", "MutationFramework.CoverageCollector", "Microsoft.TestPlatform", "System.Reflection"
    };
    Assert.That(mutatorSource.Any(line => forbiddenMutatorReferences.Any(reference => line.Contains(reference, StringComparison.Ordinal))),
        Is.False, "Mutators must generate descriptions through their typed contract and leave rewriting/execution to the engine.");
  }

  private static string[] ProjectReferences(XDocument document) => document.Descendants()
      .Where(element => element.Name.LocalName == "ProjectReference")
      .Select(element => (string?)element.Attribute("Include"))
      .Where(include => !string.IsNullOrWhiteSpace(include))
      .Select(include => Path.GetFileNameWithoutExtension(include!))
      .Order(StringComparer.Ordinal)
      .ToArray();

  private static string[] ReferencedAssemblyNames(Assembly assembly) => assembly.GetReferencedAssemblies()
      .Select(reference => reference.Name ?? string.Empty)
      .Order(StringComparer.Ordinal)
      .ToArray();

  private static string FindRepositoryRoot()
  {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
      if (File.Exists(Path.Combine(directory.FullName, "MutationFramework.slnx")))
      {
        return directory.FullName;
      }
    }
    throw new DirectoryNotFoundException("Unable to locate MutationFramework.slnx from the engine test output directory.");
  }
}
