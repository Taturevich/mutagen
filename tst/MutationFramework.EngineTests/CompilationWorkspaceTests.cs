using MutationFramework.Sdk;
using NUnit.Framework;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace MutationFramework.EngineTests;

using static MutationFramework.EngineTests.TestWorkspace;

[TestFixture]
public sealed class CompilationWorkspaceTests
{
  [Test]
  [Category("EndToEnd")]
  public async Task ConcurrentDiscoverRunsSerializeProjectEvaluationPublication()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenConcurrentCompilationCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var options = new MutationOptions
      {
        ProjectPath = Path.Combine(Root, "benchmarks", "ScaleSample", "ScaleSample", "ScaleSample.csproj"),
        TestProjects = [Path.Combine(Root, "benchmarks", "ScaleSample", "ScaleSample.Tests", "ScaleSample.Tests.csproj")],
        CacheDirectory = Path.Combine(root, "cache")
      };

      var results = await Task.WhenAll(MutationEngine.DiscoverAsync(options), MutationEngine.DiscoverAsync(options));

      Assert.That(results[0].Candidates.Select(candidate => candidate.Id).Order(StringComparer.Ordinal),
          Is.EqualTo(results[1].Candidates.Select(candidate => candidate.Id).Order(StringComparer.Ordinal)));
      Assert.That(results[0].Tests.Select(test => test.Id).Order(StringComparer.Ordinal),
          Is.EqualTo(results[1].Tests.Select(test => test.Id).Order(StringComparer.Ordinal)));
      Assert.That(results.SelectMany(result => result.Diagnostics)
          .Any(diagnostic => diagnostic.StartsWith("CacheHit: project-evaluation", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, results.SelectMany(result => result.Diagnostics)));
      Assert.That(results.SelectMany(result => result.Diagnostics)
          .All(diagnostic => !diagnostic.StartsWith("CacheHit: compilation-artifacts", StringComparison.Ordinal)), Is.True);
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("Component")]
  public async Task ProjectGraphIncludesTransitiveCompileItemsAndAssemblyQualifiedIds()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenProjectGraph", Guid.NewGuid().ToString("N"));
    var sharedDirectory = Path.Combine(root, "Shared");
    var appDirectory = Path.Combine(root, "App");
    Directory.CreateDirectory(sharedDirectory);
    Directory.CreateDirectory(appDirectory);
    try
    {
      var sharedProject = Path.Combine(sharedDirectory, "Shared.csproj");
      var sharedSource = Path.Combine(sharedDirectory, "Shared.cs");
      var appProject = Path.Combine(appDirectory, "App.csproj");
      var appSource = Path.Combine(appDirectory, "App.cs");
      File.WriteAllText(sharedProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>Shared.Product</AssemblyName><ImplicitUsings>enable</ImplicitUsings><DefineConstants>SHARED_FEATURE</DefineConstants></PropertyGroup></Project>");
      File.WriteAllText(sharedSource, "#if SHARED_FEATURE\nnamespace Shared; public static class Operations { public static int Add(int a, int b) => a + b; }\n#endif");
      File.WriteAllText(appProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>Application.Product</AssemblyName><DefineConstants>APP_FEATURE</DefineConstants></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Shared\\Shared.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(appSource, "#if APP_FEATURE\npublic static class App { public static int Use(int a, int b) => Shared.Operations.Add(a, b); }\n#endif");
      var cache = new ArtifactCache(Path.Combine(root, ".mutagen-cache"));
      var diagnostics = new List<string>();
      var evaluation = await CompilationWorkspace.GetProjectGraph(appProject, new RunMetrics(), CancellationToken.None, null, cache, diagnostics);
      Assert.That(evaluation.CompileFiles, Does.Contain(sharedSource));
      Assert.That(evaluation.CompileFiles, Does.Contain(appSource));
      Assert.That(evaluation.AssemblyNamesByFile[sharedSource], Is.EqualTo("Shared.Product"));
      Assert.That(evaluation.AssemblyNamesByFile[appSource], Is.EqualTo("Application.Product"));
      Assert.That(evaluation.ParseOptionsByFile[sharedSource].PreprocessorSymbolNames, Does.Contain("SHARED_FEATURE"));
      Assert.That(evaluation.ParseOptionsByFile[appSource].PreprocessorSymbolNames, Does.Contain("APP_FEATURE"));
      Assert.That(evaluation.ProjectPaths, Has.Length.EqualTo(2));
      var candidates = MutationDiscovery.DiscoverCandidates(evaluation.CompileFiles, appProject,
          new MutationOptions { ProjectPath = appProject, TestProjects = [] }, [], CancellationToken.None,
          context: MutationCompilationContext.From(evaluation));
      Assert.That(candidates, Has.Count.EqualTo(1));
      Assert.That(candidates[0].File, Does.Contain("Shared"));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("Component")]
  public async Task MultipleProductionProjectRootsAreMergedWithDistinctAssemblyIdentities()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenMultipleProducts", Guid.NewGuid().ToString("N"));
    var firstDirectory = Path.Combine(root, "First");
    var secondDirectory = Path.Combine(root, "Second");
    Directory.CreateDirectory(firstDirectory);
    Directory.CreateDirectory(secondDirectory);
    try
    {
      var firstProject = Path.Combine(firstDirectory, "First.csproj");
      var firstSource = Path.Combine(firstDirectory, "First.cs");
      var secondProject = Path.Combine(secondDirectory, "Second.csproj");
      var secondSource = Path.Combine(secondDirectory, "Second.cs");
      File.WriteAllText(firstProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>First.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(secondProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>Second.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(firstSource, "public static class First { public static int Apply(int a, int b) => a + b; }");
      File.WriteAllText(secondSource, "public static class Second { public static int Apply(int a, int b) => a * b; }");
      var evaluation = await CompilationWorkspace.GetProductionGraph(firstProject, [secondProject], new RunMetrics(), CancellationToken.None,
          null, new ArtifactCache(Path.Combine(root, ".mutagen-cache")), []);
      Assert.That(evaluation.CompileFiles, Does.Contain(firstSource));
      Assert.That(evaluation.CompileFiles, Does.Contain(secondSource));
      Assert.That(evaluation.AssemblyNamesByFile[firstSource], Is.EqualTo("First.Product"));
      Assert.That(evaluation.AssemblyNamesByFile[secondSource], Is.EqualTo("Second.Product"));
      var candidates = MutationDiscovery.DiscoverCandidates(evaluation.CompileFiles, firstProject,
          new MutationOptions { ProjectPath = firstProject, TestProjects = [] }, [], CancellationToken.None,
          context: MutationCompilationContext.From(evaluation));
      Assert.That(candidates, Has.Count.EqualTo(2));
      Assert.That(candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(2));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("Component")]
  public void SourceRewriterRejectsTargetsOutsideIsolatedWorkspace()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenRewriteBoundary", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var workspace = Path.Combine(root, "workspace");
      Directory.CreateDirectory(workspace);
      var original = Path.Combine(root, "Original.cs");
      var escapedTarget = Path.Combine(root, "Escaped.cs");
      const string sentinel = "public static class MustNotChange { }";
      File.WriteAllText(original, "public static class Original { }");
      File.WriteAllText(escapedTarget, sentinel);

      var exception = Assert.Throws<InvalidOperationException>(() =>
          MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [original] = escapedTarget }, [], root, workspace));

      Assert.That(exception!.Message, Does.Contain("escapes the isolated workspace"));
      Assert.That(File.ReadAllText(escapedTarget), Is.EqualTo(sentinel));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("Component")]
  public async Task LinkedCompileItemsAreEvaluatedAndRemappedIntoIsolatedWorkspace()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenLinkedSource", Guid.NewGuid().ToString("N"));
    var solution = Path.Combine(root, "solution");
    var projectDirectory = Path.Combine(solution, "Product");
    var sharedDirectory = Path.Combine(root, "shared");
    Directory.CreateDirectory(projectDirectory);
    Directory.CreateDirectory(sharedDirectory);
    try
    {
      var project = Path.Combine(projectDirectory, "Product.csproj");
      var consumerDirectory = Path.Combine(solution, "Consumer");
      Directory.CreateDirectory(consumerDirectory);
      var consumerProject = Path.Combine(consumerDirectory, "Consumer.csproj");
      var sharedProps = Path.Combine(solution, "Directory.Build.props");
      var source = Path.Combine(sharedDirectory, "Shared.cs");
      var otherSource = Path.Combine(sharedDirectory, "SharedOther.cs");
      var workspace = Path.Combine(root, "workspace");
      var copiedProject = Path.Combine(workspace, "Product", "Product.csproj");
      Directory.CreateDirectory(Path.GetDirectoryName(copiedProject)!);
      Directory.CreateDirectory(Path.Combine(workspace, "Consumer"));
      File.WriteAllText(sharedProps,
          "<Project><PropertyGroup><SharedSourcePattern>..\\..\\shared\\*.cs</SharedSourcePattern></PropertyGroup><ItemGroup><Compile Include=\"$(SharedSourcePattern)\" /></ItemGroup></Project>");
      File.WriteAllText(consumerProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      const string sourceText = "public static class Shared { public static int Value => 42; }";
      const string otherSourceText = "public static class SharedOther { public static int Value => 24; }";
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(source, sourceText);
      File.WriteAllText(otherSource, otherSourceText);
      File.Copy(project, copiedProject);
      File.Copy(consumerProject, Path.Combine(workspace, "Consumer", "Consumer.csproj"));
      File.Copy(sharedProps, Path.Combine(workspace, "Directory.Build.props"));

      var evaluation = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, "net11.0");
      var consumerEvaluation = await CompilationWorkspace.GetCompileFiles(consumerProject, new RunMetrics(), CancellationToken.None, "net11.0");
      var linkedItems = evaluation.LinkedCompileItems.Concat(consumerEvaluation.LinkedCompileItems).ToArray();
      var linkedItem = linkedItems.First();
      Assert.That(linkedItem.File, Is.EqualTo(source));

      var mapped = MutationWorkspace.MaterializeLinkedSources(workspace, solution, linkedItems);

      Assert.That(mapped[source], Does.StartWith(workspace));
      Assert.That(File.ReadAllText(mapped[source]), Is.EqualTo(sourceText));
      var remappedProject = XDocument.Load(copiedProject);
      var includes = remappedProject.Descendants().Where(element => element.Name.LocalName == "Compile" && element.Attribute("Include") is not null)
          .Select(element => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(copiedProject)!, element.Attribute("Include")!.Value))).ToArray();
      Assert.That(includes, Does.Contain(mapped[source]).And.Contain(mapped[otherSource]));
      Assert.That(File.ReadAllText(Path.Combine(workspace, "Directory.Build.props")), Does.Contain("$(SharedSourcePattern)"));
      Assert.That(File.ReadAllText(mapped[otherSource]), Is.EqualTo(otherSourceText));
      Assert.That(File.ReadAllText(source), Is.EqualTo(sourceText));
      var remappedEvaluation = await CompilationWorkspace.GetCompileFiles(copiedProject, new RunMetrics(), CancellationToken.None, "net11.0");
      Assert.That(remappedEvaluation.CompileFiles, Does.Contain(mapped[source]));
      Assert.That(remappedEvaluation.CompileFiles, Does.Contain(mapped[otherSource]));
      var consumerEvaluationAfterRemap = await CompilationWorkspace.GetCompileFiles(consumerProject.Replace(solution, workspace, StringComparison.Ordinal),
          new RunMetrics(), CancellationToken.None, "net11.0");
      Assert.That(consumerEvaluationAfterRemap.CompileFiles, Does.Contain(mapped[source]));
      Assert.That(consumerEvaluationAfterRemap.CompileFiles, Does.Contain(mapped[otherSource]));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task LinkedProductionSourceMutatesInTemporaryWorkspaceAndIsKilledByItsTest()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenLinkedSourceE2E", Guid.NewGuid().ToString("N"));
    var solution = Path.Combine(root, "solution");
    var appDirectory = Path.Combine(solution, "Product");
    var testDirectory = Path.Combine(solution, "Product.Tests");
    var sharedDirectory = Path.Combine(root, "shared");
    Directory.CreateDirectory(appDirectory);
    Directory.CreateDirectory(testDirectory);
    Directory.CreateDirectory(sharedDirectory);
    var output = Path.Combine(root, "results");
    try
    {
      var appProject = Path.Combine(appDirectory, "Product.csproj");
      var testProject = Path.Combine(testDirectory, "Product.Tests.csproj");
      var linkedSource = Path.Combine(sharedDirectory, "LinkedCalculator.cs");
      File.WriteAllText(Path.Combine(solution, "MutationFixture.slnx"), "<Solution />");
      File.WriteAllText(appProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>LinkedProduct</AssemblyName><SharedSources>..\\..\\shared\\*.cs</SharedSources></PropertyGroup><ItemGroup><Compile Include=\"$(SharedSources)\" Link=\"%(Filename)%(Extension)\" /></ItemGroup></Project>");
      File.WriteAllText(linkedSource,
          "public static class LinkedCalculator { public static int AddOne(int value) => value + 1; }");
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Product\\Product.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testDirectory, "LinkedCalculatorTests.cs"),
          "using NUnit.Framework; public sealed class LinkedCalculatorTests { [Test] public void LinkedSourceArithmeticIsKilled() => Assert.That(LinkedCalculator.AddOne(2), Is.EqualTo(3)); }");

      using (var restore = new Process())
      {
        restore.StartInfo = new ProcessStartInfo("dotnet")
        {
          WorkingDirectory = solution,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true
        };
        restore.StartInfo.ArgumentList.Add("restore");
        restore.StartInfo.ArgumentList.Add(testProject);
        restore.StartInfo.ArgumentList.Add("--ignore-failed-sources");
        restore.StartInfo.ArgumentList.Add("-p:NuGetAudit=false");
        Assert.That(restore.Start(), Is.True);
        var stdout = restore.StandardOutput.ReadToEndAsync();
        var stderr = restore.StandardError.ReadToEndAsync();
        await restore.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Assert.That(restore.ExitCode, Is.Zero, (await stdout) + Environment.NewLine + await stderr);
      }

      var options = new MutationOptions
      {
        ProjectPath = appProject,
        TestProjects = [testProject],
        Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
        TestFilter = "LinkedSourceArithmeticIsKilled",
        MaxWorkers = 1,
        OutputDirectory = output,
        CacheDirectory = Path.Combine(root, "cache"),
        TimeoutSeconds = 120
      };
      MutationRunResult result;
      try
      {
        result = await MutationEngine.RunAsync(options);
      }
      catch (BaselineFailureException exception)
      {
        Assert.Fail(string.Join(Environment.NewLine, exception.Errors));
        throw;
      }
      var repeated = await MutationEngine.RunAsync(options);

      Assert.That(result.OverallStatus, Is.EqualTo("Completed"));
      Assert.That(result.Mutants, Has.Count.EqualTo(1));
      Assert.That(result.Mutants[0].Status, Is.EqualTo(MutationStatus.Killed));
      Assert.That(result.Mutants[0].SourceFile, Does.Contain("LinkedCalculator.cs"));
      Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheMiss: mutation-compilation-artifacts", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, result.Diagnostics));
      var mutationCacheDirectory = Path.Combine(options.CacheDirectory!, "mutation-compilation-artifacts");
      var mutationCompilationKey = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(mutationCacheDirectory, "index"), "*.json").Single());
      var mutationCompilationManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(mutationCacheDirectory, mutationCompilationKey + ".json")))!;
      Assert.That(mutationCompilationManifest["Payload"]!["Files"]!.AsArray()
          .All(file => !Path.GetExtension(file!["RelativePath"]!.GetValue<string>()).Equals(".pdb", StringComparison.OrdinalIgnoreCase)), Is.True,
          "Cached PDBs retain temporary workspace paths, so mutation compilation artifacts omit debug symbols.");
      Assert.That(repeated.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheHit: mutation-compilation-artifacts", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, repeated.Diagnostics));
      Assert.That(repeated.Counters.BuildInvocations, Is.Zero,
          "An identical mutation schema and evaluated project graph should restore its validated test compilation outputs.");
      Assert.That(repeated.Mutants.Select(mutant => (mutant.Id, mutant.Status)), Is.EqualTo(result.Mutants.Select(mutant => (mutant.Id, mutant.Status))));

      mutationCompilationKey = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(mutationCacheDirectory, "index"), "*.json").Single());
      var mutationManifestPath = Path.Combine(mutationCacheDirectory, mutationCompilationKey + ".json");
      mutationCompilationManifest = JsonNode.Parse(File.ReadAllText(mutationManifestPath))!;
      mutationCompilationManifest["Payload"]!["Files"]![0]!["RelativePath"] = "../escaped-output.dll";
      mutationCompilationManifest["IntegrityHash"] = CacheIdentity.Hash(mutationCompilationManifest["Payload"]!.ToJsonString());
      File.WriteAllText(mutationManifestPath, mutationCompilationManifest.ToJsonString());
      var unsafeManifest = await MutationEngine.RunAsync(options);
      Assert.That(unsafeManifest.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: mutation-compilation-artifacts manifest contains an unsafe path", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, unsafeManifest.Diagnostics));
      Assert.That(unsafeManifest.Mutants.Single().Status, Is.EqualTo(MutationStatus.Killed));

      mutationCompilationKey = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(mutationCacheDirectory, "index"), "*.json").Single());
      mutationManifestPath = Path.Combine(mutationCacheDirectory, mutationCompilationKey + ".json");
      mutationCompilationManifest = JsonNode.Parse(File.ReadAllText(mutationManifestPath))!;
      var mutationManifestFiles = mutationCompilationManifest["Payload"]!["Files"]!.AsArray();
      var originalMutationBlob = mutationManifestFiles[0]!["BlobKey"]!.GetValue<string>();
      var alternateMutationBlob = mutationManifestFiles.Select(file => file!["BlobKey"]!.GetValue<string>())
          .First(blob => !string.Equals(blob, originalMutationBlob, StringComparison.Ordinal));
      mutationManifestFiles[0]!["BlobKey"] = alternateMutationBlob;
      mutationCompilationManifest["IntegrityHash"] = CacheIdentity.Hash(mutationCompilationManifest["Payload"]!.ToJsonString());
      File.WriteAllText(mutationManifestPath, mutationCompilationManifest.ToJsonString());
      var tamperedManifest = await MutationEngine.RunAsync(options);
      Assert.That(tamperedManifest.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: mutation-compilation-artifacts output manifest integrity check failed", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, tamperedManifest.Diagnostics));
      Assert.That(tamperedManifest.Mutants.Single().Status, Is.EqualTo(MutationStatus.Killed));

      File.WriteAllText(linkedSource,
          "public static class LinkedCalculator { public static int AddOne(int value) => value + 2; }");
      File.WriteAllText(Path.Combine(testDirectory, "LinkedCalculatorTests.cs"),
          "using NUnit.Framework; public sealed class LinkedCalculatorTests { [Test] public void LinkedSourceArithmeticIsKilled() => Assert.That(LinkedCalculator.AddOne(2), Is.EqualTo(4)); }");
      var changed = await MutationEngine.RunAsync(options);
      Assert.That(changed.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: mutation-discovery", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, changed.Diagnostics));
      Assert.That(changed.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: mutation-compilation-artifacts", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, changed.Diagnostics));
      Assert.That(changed.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheHit: coverage-maps", StringComparison.Ordinal)), Is.True,
          "The baseline reruns and reports the same runtime-site IDs, so only their deterministic ID-to-site projection can be reused.");
      Assert.That(changed.Mutants.Single().Status, Is.EqualTo(MutationStatus.Killed));
      Assert.That(File.ReadAllText(linkedSource), Does.Contain("value + 2"));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("Component")]
  public async Task ProjectEvaluationUsesSelectedTargetFrameworkLanguageVersionAndDefines()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenProjectEvaluation", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "MultiTarget.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks><LangVersion>12.0</LangVersion><DefineConstants>FEATURE_X</DefineConstants><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Fixture.cs\" /></ItemGroup></Project>");
      File.WriteAllText(source, "public class Fixture { }");

      var missingSelection = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
          await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None));
      Assert.That(missingSelection!.Message, Does.Contain("Select one with --framework"));

      var evaluated = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, "net9.0");

      Assert.That(evaluated.CompileFiles, Is.EqualTo(new[] { source }));
      Assert.That(evaluated.TargetFramework, Is.EqualTo("net9.0"));
      Assert.That(evaluated.ParseOptions.LanguageVersion, Is.EqualTo(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp12));
      Assert.That(evaluated.ParseOptions.PreprocessorSymbolNames, Does.Contain("FEATURE_X"));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("Component")]
  public async Task ProjectEvaluationResolvesTestTargetPathFromDirectoryBuildProps()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenDirectoryBuildProps", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Custom.Tests.csproj");
      File.WriteAllText(Path.Combine(root, "Directory.Build.props"),
          "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>Company.Custom.Tests</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(project,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>");

      var cache = new ArtifactCache(Path.Combine(root, ".mutagen-cache"));
      var diagnostics = new List<string>();
      var evaluation = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);

      Assert.That(evaluation.TargetFramework, Is.EqualTo("net11.0"));
      Assert.That(evaluation.AssemblyName, Is.EqualTo("Company.Custom.Tests"));
      Assert.That(evaluation.TargetPath, Does.EndWith(Path.Combine("net11.0", "Company.Custom.Tests.dll")));
      diagnostics.Clear();
      var cached = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(cached.TargetPath, Is.EqualTo(evaluation.TargetPath));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: project-evaluation", StringComparison.Ordinal)), Is.True);
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("Component")]
  public async Task ProjectEvaluationHonorsConditionalDirectoryBuildPropsAndTargets()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenConditionalProjectGraph", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Conditional.csproj");
      var baseSource = Path.Combine(root, "Base.cs");
      var net8Source = Path.Combine(root, "Net8Only.cs");
      var net9Source = Path.Combine(root, "Net9Only.cs");
      File.WriteAllText(project,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Base.cs\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(root, "Directory.Build.props"),
          "<Project><PropertyGroup Condition=\"'$(TargetFramework)' == 'net8.0'\"><DefineConstants>$(DefineConstants);TARGET_NET8</DefineConstants></PropertyGroup><PropertyGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><DefineConstants>$(DefineConstants);TARGET_NET9</DefineConstants></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(root, "Directory.Build.targets"),
          "<Project><ItemGroup Condition=\"'$(TargetFramework)' == 'net8.0'\"><Compile Include=\"Net8Only.cs\" /></ItemGroup><ItemGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><Compile Include=\"Net9Only.cs\" /></ItemGroup></Project>");
      File.WriteAllText(baseSource, "public static class Base { }");
      File.WriteAllText(net8Source, "public static class Net8Only { }");
      File.WriteAllText(net9Source, "public static class Net9Only { }");

      var net8 = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, "net8.0");
      var net9 = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, "net9.0");

      Assert.That(net8.CompileFiles, Is.EquivalentTo(new[] { baseSource, net8Source }));
      Assert.That(net9.CompileFiles, Is.EquivalentTo(new[] { baseSource, net9Source }));
      Assert.That(net8.ParseOptions.PreprocessorSymbolNames, Does.Contain("TARGET_NET8"));
      Assert.That(net8.ParseOptions.PreprocessorSymbolNames, Does.Not.Contain("TARGET_NET9"));
      Assert.That(net9.ParseOptions.PreprocessorSymbolNames, Does.Contain("TARGET_NET9"));
      Assert.That(net9.ParseOptions.PreprocessorSymbolNames, Does.Not.Contain("TARGET_NET8"));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("Component")]
  public void ProjectTreeRootUsesEvaluatedProjectGraphAndRetainsNuGetConfiguration()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenProjectTreeRoot", Guid.NewGuid().ToString("N"));
    var solutionRoot = Path.Combine(root, "solution");
    var sharedProjectRoot = Path.Combine(solutionRoot, "services");
    var productDirectory = Path.Combine(sharedProjectRoot, "Product");
    var testDirectory = Path.Combine(sharedProjectRoot, "Tests");
    Directory.CreateDirectory(productDirectory);
    Directory.CreateDirectory(testDirectory);
    try
    {
      File.WriteAllText(Path.Combine(solutionRoot, "Repository.slnx"), "<Solution />");
      var product = Path.Combine(productDirectory, "Product.csproj");
      var tests = Path.Combine(testDirectory, "Product.Tests.csproj");
      Assert.That(MutationWorkspace.FindProjectTreeRoot([product, tests]), Is.EqualTo(sharedProjectRoot));

      File.WriteAllText(Path.Combine(root, "NuGet.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
      Assert.That(MutationWorkspace.FindProjectTreeRoot([product, tests]), Is.EqualTo(root));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("Component")]
  public async Task ScaleSampleProjectGraphKeepsWorkspaceCopyRootInsideTheBenchmarkTree()
  {
    var cacheRoot = Path.Combine(Path.GetTempPath(), "MutagenScaleProjectTree", Guid.NewGuid().ToString("N"));
    try
    {
      var projectDirectory = Path.Combine(Root, "benchmarks", "ScaleSample");
      var productionProject = Path.Combine(projectDirectory, "ScaleSample", "ScaleSample.csproj");
      var testsProject = Path.Combine(projectDirectory, "ScaleSample.Tests", "ScaleSample.Tests.csproj");
      var cache = new ArtifactCache(cacheRoot);
      var diagnostics = new List<string>();
      var production = await CompilationWorkspace.GetProjectGraph(productionProject, new RunMetrics(), CancellationToken.None,
          "net11.0", cache, diagnostics);
      var tests = await CompilationWorkspace.GetProjectGraph(testsProject, new RunMetrics(), CancellationToken.None,
          "net11.0", cache, diagnostics);
      var graphProjects = production.ProjectPaths.Concat(tests.ProjectPaths)
          .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

      Assert.That(graphProjects, Has.Length.EqualTo(2));
      Assert.That(MutationWorkspace.FindProjectTreeRoot(graphProjects), Is.EqualTo(projectDirectory));
    }
    finally
    {
      if (Directory.Exists(cacheRoot))
      {
        Directory.Delete(cacheRoot, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task ExternalDirectoryBuildPropsTargetsAndLinkedSourcesWorkInMutationWorkspace()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenExternalBuildConfiguration", Guid.NewGuid().ToString("N"));
    var solutionRoot = Path.Combine(root, "solution");
    var productDirectory = Path.Combine(solutionRoot, "Product");
    var testsDirectory = Path.Combine(solutionRoot, "Tests");
    var sharedDirectory = Path.Combine(root, "shared");
    Directory.CreateDirectory(productDirectory);
    Directory.CreateDirectory(testsDirectory);
    Directory.CreateDirectory(sharedDirectory);
    try
    {
      var productProject = Path.Combine(productDirectory, "Product.csproj");
      var testsProject = Path.Combine(testsDirectory, "Product.Tests.csproj");
      var linkedSource = Path.Combine(sharedDirectory, "LinkedConstants.cs");
      File.WriteAllText(Path.Combine(solutionRoot, "ExternalConfiguration.slnx"), "<Solution />");
      File.WriteAllText(productProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productDirectory, "Product.cs"),
          "#if MUTAGEN_EXTERNAL_CONFIGURATION\npublic static class Product { public static int Value => 40 + 2; }\n#else\n#error External Directory.Build.props was not imported.\n#endif\n");
      File.WriteAllText(testsProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><IsTestProject>true</IsTestProject><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Product\\Product.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(root, "Directory.Build.props"),
          "<Project><PropertyGroup><DefineConstants>$(DefineConstants);MUTAGEN_EXTERNAL_CONFIGURATION</DefineConstants><AssemblyName Condition=\"'$(MSBuildProjectName)' == 'Product'\">ExternalCompany.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(root, "Directory.Build.targets"),
          "<Project><ItemGroup Condition=\"'$(MSBuildProjectName)' == 'Product'\"><Compile Include=\"$(MSBuildThisFileDirectory)shared\\LinkedConstants.cs\" /></ItemGroup><Target Name=\"ValidateExternalConfiguration\" BeforeTargets=\"CoreCompile\" Condition=\"'$(MSBuildProjectName)' == 'Product'\"><Error Condition=\"'$(AssemblyName)' != 'ExternalCompany.Product'\" Text=\"External Directory.Build.props was not loaded by the isolated build.\" /></Target></Project>");
      File.WriteAllText(linkedSource,
          "public static class LinkedConstants { public static int Value => 24; }\n");
      File.WriteAllText(Path.Combine(testsDirectory, "ProductTests.cs"),
          "using NUnit.Framework; public sealed class ProductTests { [Test] public void ValueUsesExternalConfigurationAndLinkedSource() { Assert.That(typeof(Product).Assembly.Location, Does.Contain(\"mutation-framework\")); Assert.That(Product.Value, Is.EqualTo(42)); Assert.That(LinkedConstants.Value, Is.EqualTo(24)); } }");

      var result = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productProject,
        TestProjects = [testsProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
        MaxWorkers = 1,
        TimeoutSeconds = 120,
        OutputDirectory = Path.Combine(root, "mutation-output"),
        CacheDirectory = Path.Combine(root, "mutation-cache")
      });

      Assert.That(result.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, result.Diagnostics));
      Assert.That(result.Mutants, Has.Count.EqualTo(1));
      Assert.That(result.KilledCount, Is.EqualTo(1),
          $"Status: {result.Mutants[0].Status}; diagnostic: {result.Mutants[0].Diagnostic}; coverage: {string.Join(",", result.CoverageStatuses.Select(pair => $"{pair.Key}={pair.Value}"))}; diagnostics: {string.Join(Environment.NewLine, result.Diagnostics)}");
      Assert.That(result.Mutants[0].SourceFile, Does.EndWith("Product.cs"));
      Assert.That(File.ReadAllText(linkedSource), Does.Contain("public static int Value => 24"));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task MultipleProductionProjectsBuildAndExecuteTheirMutations()
  {
    var output = NewOutputDirectory();
    try
    {
      var scaleProject = Path.Combine(Root, "benchmarks", "ScaleSample", "ScaleSample", "ScaleSample.csproj");
      var scaleTests = Path.Combine(Root, "benchmarks", "ScaleSample", "ScaleSample.Tests", "ScaleSample.Tests.csproj");
      var options = new MutationOptions
      {
        ProjectPath = AppProject,
        ProductionProjects = [scaleProject],
        TestProjects = [TestProject, scaleTests],
        Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
        TestFilter = "Add_ReturnsCorrectSum;SingleCaseCoversMutations",
        MaxWorkers = 2,
        OutputDirectory = output,
        TimeoutSeconds = 120
      };

      var result = await MutationEngine.RunAsync(options);

      Assert.That(result.OverallStatus, Is.EqualTo("Completed"));
      Assert.That(result.Tests.Select(test => test.DisplayName).Any(name => name.Contains("Add_ReturnsCorrectSum", StringComparison.Ordinal)), Is.True);
      Assert.That(result.Tests.Select(test => test.DisplayName).Any(name => name.Contains("SingleCaseCoversMutations", StringComparison.Ordinal)), Is.True);
      Assert.That(result.Mutants.Any(mutation => mutation.SourceFile.EndsWith("Calculator.cs", StringComparison.Ordinal)), Is.True);
      Assert.That(result.Mutants.Any(mutation => mutation.SourceFile.Contains("ScaleSample/ThresholdOperations.cs", StringComparison.Ordinal)), Is.True);
      Assert.That(result.Mutants.Any(mutation => mutation.SourceFile.EndsWith("Calculator.cs", StringComparison.Ordinal) && mutation.Status is MutationStatus.Killed or MutationStatus.Survived), Is.True);
      Assert.That(result.Mutants.Any(mutation => mutation.SourceFile.Contains("ScaleSample/ThresholdOperations.cs", StringComparison.Ordinal) && mutation.Status is MutationStatus.Killed or MutationStatus.Survived), Is.True);
      Assert.That(result.Mutants.All(mutation => mutation.Status is MutationStatus.Killed or MutationStatus.Survived or MutationStatus.NoCoverage or MutationStatus.CompileError), Is.True);
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }

}
