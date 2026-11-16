using MutationFramework.Sdk;
using NUnit.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class MutationEngineIntegrationTests
{
  private static readonly string Root = FindRepositoryRoot();
  private static string AppProject => Path.Combine(Root, "samples", "SampleApplication", "SampleApplication.csproj");
  private static string TestProject => Path.Combine(Root, "samples", "SampleApplication.Tests", "SampleApplication.Tests.csproj");

  [Test]
  public void MutatorRegistryHasStableExplicitSelectionAndRejectsUnknownIds()
  {
    var all = MutatorRegistry.CreateDefault();
    Assert.That(all.Mutators.Select(x => x.Id), Is.EqualTo(new[] { "assignment-expressions", "binary-operators", "binary-patterns", "boolean-conditions", "boolean-literals", "checked-contexts", "collection-expressions", "conditional-expressions", "initialization", "is-patterns", "linq-methods", "math-methods", "null-coalescing", "regex-patterns", "statement-removal", "string-method-constants", "string-methods", "string-values", "unary-update" }));
    Assert.That(all.Mutators.Select(x => x.SchemaVersion), Is.All.EqualTo(1));
    Assert.That(all.Mutators.SelectMany(x => x.SupportedConstructs), Is.Not.Empty);
    Assert.That(all.Mutators.Single(x => x.Id == "binary-operators").Categories,
        Is.EquivalentTo(new[] { MutationCategory.Arithmetic, MutationCategory.Equality, MutationCategory.Logical, MutationCategory.Bitwise }));
    var selected = MutatorRegistry.CreateDefault(new HashSet<string>(["boolean-literals"], StringComparer.Ordinal));
    Assert.That(selected.Mutators.Select(x => x.Id), Is.EqualTo(new[] { "boolean-literals" }));
    Assert.Throws<ArgumentException>(() => MutatorRegistry.CreateDefault(new HashSet<string>(["typo"], StringComparer.Ordinal)));
  }

  [Test]
  public void MutatorAssemblyDependsOnlyOnRoslynAndFrameworkLibraries()
  {
    var mutatorAssembly = typeof(MutatorRegistry).Assembly;
    var references = mutatorAssembly.GetReferencedAssemblies().Select(assembly => assembly.Name).ToArray();
    Assert.That(references, Does.Not.Contain("MutationFramework.Cli"));
    Assert.That(references, Does.Not.Contain("Microsoft.TestPlatform.TranslationLayer"));
    Assert.That(mutatorAssembly.GetName().Name, Is.EqualTo("MutationFramework.Mutators"));
    Assert.That(typeof(MutationEngine).Assembly.GetReferencedAssemblies().Select(assembly => assembly.Name),
        Does.Contain("MutationFramework.Mutators"));
  }

  [Test]
  public void TestProfilesDistinguishPerTestPerMutantAndMixedExecutionPolicies()
  {
    Assert.That(Engine.ResolveTestExecutionPolicies(true, false),
        Is.EqualTo((TestHostIsolationPolicy.PerTest, TestRunTimeoutPolicy.PerTest)));
    Assert.That(Engine.ResolveTestExecutionPolicies(false, true),
        Is.EqualTo((TestHostIsolationPolicy.PerMutant, TestRunTimeoutPolicy.AggregatePerMutant)));
    Assert.That(Engine.ResolveTestExecutionPolicies(false, false),
        Is.EqualTo((TestHostIsolationPolicy.PerMutant, TestRunTimeoutPolicy.AggregatePerMutant)));
    Assert.That(Engine.ResolveTestExecutionPolicies(true, true),
        Is.EqualTo((TestHostIsolationPolicy.Mixed, TestRunTimeoutPolicy.Mixed)));
  }

  [Test]
  public void MutationOptionsDefaultToOneFreshTestHostPerMutant()
  {
    var options = new MutationOptions { ProjectPath = "production.csproj", TestProjects = ["tests.csproj"] };
    Assert.That(options.TestHostIsolationPolicy, Is.EqualTo(TestHostIsolationPolicy.PerMutant));
  }

  [Test]
  public void ResourceLockConfigurationMustNameDiscoveredTestsAndResources()
  {
    var configured = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = ["shared-database"]
    };
    Assert.DoesNotThrow(() => Engine.ValidateResourceLockIdentities(configured, ["test-a", "test-b"]));
    var unknown = Assert.Throws<ArgumentException>(() => Engine.ValidateResourceLockIdentities(configured, ["test-b"]));
    Assert.That(unknown!.Message, Does.Contain("test-a"));

    var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = []
    };
    Assert.Throws<ArgumentException>(() => Engine.ValidateResourceLockIdentities(empty, ["test-a"]));

    var blankName = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = [" "]
    };
    Assert.Throws<ArgumentException>(() => Engine.ValidateResourceLockIdentities(blankName, ["test-a"]));

    var nullList = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = null!
    };
    Assert.Throws<ArgumentException>(() => Engine.ValidateResourceLockIdentities(nullList, ["test-a"]));
  }

  [Test]
  public void GeneratedCompileItemsRemainInSemanticContextButAreNeverMutated()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenGeneratedSources", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Generated.csproj");
      var regular = Path.Combine(root, "Regular.cs");
      var generatedByName = Path.Combine(root, "Widget.Designer.cs");
      var generatedByHeader = Path.Combine(root, "GeneratedByTool.cs");
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(regular, "public static class Regular { public static int Add(int left, int right) => left + right; }");
      File.WriteAllText(generatedByName, "public static class WidgetDesigner { public static int Add(int left, int right) => left + right; }");
      File.WriteAllText(generatedByHeader, "// <auto-generated/>\npublic static class GeneratedByTool { public static int Add(int left, int right) => left + right; }");

      var candidates = Engine.DiscoverCandidates([regular, generatedByName, generatedByHeader], project,
          new MutationOptions { ProjectPath = project, TestProjects = [] }, [], CancellationToken.None);

      Assert.That(candidates, Is.Not.Empty);
      Assert.That(candidates.Select(candidate => Path.GetFileName(candidate.File)), Is.All.EqualTo("Regular.cs"));
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
  public async Task ArtifactCacheHitsIdenticalInputsAndInvalidatesChangedContent()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenArtifactCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var source = Path.Combine(root, "Source.cs");
      File.WriteAllText(source, "class Source { }");
      var cache = new ArtifactCache(Path.Combine(root, "cache"));
      var logical = Path.Combine(root, "Product.csproj");
      var firstKey = ArtifactCache.ContentKey([source], ["discovery-v1", "net11.0"]);
      var miss = await cache.TryReadAsync<string>("test-phase", logical, firstKey);
      Assert.That(miss.Found, Is.False);
      Assert.That(miss.Diagnostic, Does.StartWith("CacheMiss:"));
      await cache.WriteAsync("test-phase", logical, firstKey, "cached-result");
      var hit = await cache.TryReadAsync<string>("test-phase", logical, firstKey);
      Assert.That(hit.Found, Is.True);
      Assert.That(hit.Value, Is.EqualTo("cached-result"));
      Assert.That(hit.Diagnostic, Does.StartWith("CacheHit:"));
      File.WriteAllText(source, "class Source { int Changed => 1; }");
      var changedKey = ArtifactCache.ContentKey([source], ["discovery-v1", "net11.0"]);
      var invalidated = await cache.TryReadAsync<string>("test-phase", logical, changedKey);
      Assert.That(invalidated.Found, Is.False);
      Assert.That(invalidated.Diagnostic, Does.StartWith("CacheInvalidated:"));

      var blobKey = await cache.StoreBlobAsync("compilation-artifacts", source);
      var blobPath = Directory.EnumerateFiles(Path.Combine(root, "cache", "compilation-artifacts", "blobs")).Single();
      File.WriteAllText(blobPath, "corrupt output");
      var restored = Path.Combine(root, "restored.cs");
      Assert.That(await cache.TryCopyBlobAsync("compilation-artifacts", blobKey, restored), Is.False);
      Assert.That(await cache.StoreBlobAsync("compilation-artifacts", source), Is.EqualTo(blobKey));
      Assert.That(await cache.TryCopyBlobAsync("compilation-artifacts", blobKey, restored), Is.True);
      Assert.That(File.ReadAllText(restored), Is.EqualTo(File.ReadAllText(source)));
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
  public async Task ArtifactCacheConcurrentIndexReplacementNeverReturnsAnotherContentKey()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenConcurrentArtifactCache", Guid.NewGuid().ToString("N"));
    var cacheRoot = Path.Combine(root, "cache");
    const string phase = "concurrent-test-phase";
    var logicalIdentity = Path.Combine(root, "Product.csproj");
    const int writers = 8;
    const int writesPerWriter = 20;
    const int readers = 8;
    const int readsPerReader = 100;
    var keys = Enumerable.Range(0, writers)
        .Select(index => ArtifactCache.ContentKey([], ["parallel-index-v1", index.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
        .ToArray();
    var values = Enumerable.Range(0, writers).Select(index => $"value-{index}").ToArray();
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tasks = new List<Task>();

    for (var writer = 0; writer < writers; writer++)
    {
      var writerIndex = writer;
      tasks.Add(Task.Run(async () =>
      {
        var cache = new ArtifactCache(cacheRoot);
        await start.Task;
        for (var iteration = 0; iteration < writesPerWriter; iteration++)
        {
          await cache.WriteAsync(phase, logicalIdentity, keys[writerIndex], values[writerIndex]);
          var read = await cache.TryReadAsync<string>(phase, logicalIdentity, keys[writerIndex]);
          if (read.Found && read.Value != values[writerIndex])
          {
            throw new InvalidDataException("A cache hit returned the value for a different content key.");
          }
        }
      }));
    }

    for (var reader = 0; reader < readers; reader++)
    {
      var readerIndex = reader;
      tasks.Add(Task.Run(async () =>
      {
        var cache = new ArtifactCache(cacheRoot);
        await start.Task;
        for (var iteration = 0; iteration < readsPerReader; iteration++)
        {
          var keyIndex = (readerIndex + iteration) % writers;
          var read = await cache.TryReadAsync<string>(phase, logicalIdentity, keys[keyIndex]);
          if (read.Found && read.Value != values[keyIndex])
          {
            throw new InvalidDataException("A concurrent cache hit returned the value for a different content key.");
          }
        }
      }));
    }

    start.SetResult();
    try
    {
      await Task.WhenAll(tasks);
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
  public async Task ValidatedMutationResultCacheInvalidatesChangedTestDependencyAndReusesOnlyValidatedOutcome()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenValidatedResultCache", Guid.NewGuid().ToString("N"));
    var solution = Path.Combine(root, "solution");
    var productDirectory = Path.Combine(solution, "Product");
    var oracleDirectory = Path.Combine(solution, "TestOracle");
    var testsDirectory = Path.Combine(solution, "Product.Tests");
    Directory.CreateDirectory(productDirectory);
    Directory.CreateDirectory(oracleDirectory);
    Directory.CreateDirectory(testsDirectory);
    try
    {
      var productProject = Path.Combine(productDirectory, "Product.csproj");
      var oracleProject = Path.Combine(oracleDirectory, "TestOracle.csproj");
      var testsProject = Path.Combine(testsDirectory, "Product.Tests.csproj");
      var oracleSource = Path.Combine(oracleDirectory, "Oracle.cs");
      File.WriteAllText(Path.Combine(solution, "MutationFixture.slnx"), "<Solution />");
      File.WriteAllText(productProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productDirectory, "Product.cs"),
          "public static class Product { public static int Value => 40 + 2; }");
      File.WriteAllText(oracleProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(oracleSource, "public static class Oracle { public static bool Accept(int value) => true; }");
      File.WriteAllText(testsProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Product\\Product.csproj\" /><ProjectReference Include=\"..\\TestOracle\\TestOracle.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "ProductTests.cs"),
          "using NUnit.Framework; public sealed class ProductTests { [Test] public void ProductValueIsAccepted() => Assert.That(Oracle.Accept(Product.Value), Is.True); }");

      using (var restore = new Process())
      {
        restore.StartInfo = new ProcessStartInfo("dotnet")
        {
          WorkingDirectory = solution,
          UseShellExecute = false,
          CreateNoWindow = true
        };
        restore.StartInfo.ArgumentList.Add("restore");
        restore.StartInfo.ArgumentList.Add(testsProject);
        restore.StartInfo.ArgumentList.Add("--ignore-failed-sources");
        restore.StartInfo.ArgumentList.Add("-p:NuGetAudit=false");
        Assert.That(restore.Start(), Is.True);
        await restore.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Assert.That(restore.ExitCode, Is.Zero);
      }

      var options = new MutationOptions
      {
        ProjectPath = productProject,
        TestProjects = [testsProject],
        Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
        TestFilter = "ProductValueIsAccepted",
        ExecutionMode = MutationExecutionMode.Isolated,
        TestHostIsolationPolicy = TestHostIsolationPolicy.PerTest,
        MaxWorkers = 1,
        OutputDirectory = Path.Combine(root, "results"),
        CacheDirectory = Path.Combine(root, "cache"),
        MutationResultCacheScopeKey = "oracle-fixture-v1",
        TimeoutSeconds = 120
      };

      var first = await MutationEngine.RunAsync(options);
      Assert.That(first.Mutants, Has.Count.EqualTo(1));
      Assert.That(first.Mutants[0].Status, Is.EqualTo(MutationStatus.Survived));
      Assert.That(first.Counters.MutationTestExecutions, Is.EqualTo(1));
      Assert.That(first.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheMiss: validated-mutation-results", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, first.Diagnostics));

      File.WriteAllText(oracleSource, "public static class Oracle { public static bool Accept(int value) => value == 42; }");
      var changedDependency = await MutationEngine.RunAsync(options);
      Assert.That(changedDependency.Mutants.Single().Status, Is.EqualTo(MutationStatus.Killed),
          string.Join(Environment.NewLine, changedDependency.Diagnostics));
      Assert.That(changedDependency.Counters.MutationTestExecutions, Is.EqualTo(1));
      Assert.That(changedDependency.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: validated-mutation-results", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, changedDependency.Diagnostics));

      var unchangedDependency = await MutationEngine.RunAsync(options);
      Assert.That(unchangedDependency.Mutants.Single().Status, Is.EqualTo(MutationStatus.Killed));
      Assert.That(unchangedDependency.Counters.MutationTestExecutions, Is.Zero, string.Join(Environment.NewLine, unchangedDependency.Diagnostics));
      Assert.That(unchangedDependency.Counters.BaselineTestExecutions, Is.EqualTo(1));
      Assert.That(unchangedDependency.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheHit: validated-mutation-results", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, unchangedDependency.Diagnostics));

      const string cacheEnvironmentVariable = "MUTAGEN_VALIDATED_RESULT_CACHE_TEST_CONFIGURATION";
      var previousCacheEnvironment = Environment.GetEnvironmentVariable(cacheEnvironmentVariable);
      try
      {
        Environment.SetEnvironmentVariable(cacheEnvironmentVariable, "configuration-v2");
        var changedEnvironment = await MutationEngine.RunAsync(options);
        Assert.That(changedEnvironment.Mutants.Single().Status, Is.EqualTo(MutationStatus.Killed));
        Assert.That(changedEnvironment.Counters.MutationTestExecutions, Is.EqualTo(1),
            "Validated outcomes must be re-executed when the process environment changes.");
        Assert.That(changedEnvironment.Diagnostics.Any(diagnostic =>
            diagnostic.StartsWith("CacheInvalidated: validated-mutation-results", StringComparison.Ordinal)), Is.True,
            string.Join(Environment.NewLine, changedEnvironment.Diagnostics));
      }
      finally
      {
        Environment.SetEnvironmentVariable(cacheEnvironmentVariable, previousCacheEnvironment);
      }

      var resultCacheDirectory = Path.Combine(options.CacheDirectory!, "validated-mutation-results");
      var cachedResultKey = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(resultCacheDirectory, "index"), "*.json").Single());
      var cachedResultPath = Path.Combine(resultCacheDirectory, cachedResultKey + ".json");
      var tamperedResult = JsonNode.Parse(File.ReadAllText(cachedResultPath))!;
      tamperedResult["Result"]!["Status"] = MutationStatus.Survived.ToString();
      File.WriteAllText(cachedResultPath, tamperedResult.ToJsonString());
      var repairedCache = await MutationEngine.RunAsync(options);
      Assert.That(repairedCache.Mutants.Single().Status, Is.EqualTo(MutationStatus.Killed));
      Assert.That(repairedCache.Counters.MutationTestExecutions, Is.EqualTo(1));
      Assert.That(repairedCache.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: validated-mutation-results", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, repairedCache.Diagnostics));

      var changedScope = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productProject,
        TestProjects = [testsProject],
        Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
        TestFilter = "ProductValueIsAccepted",
        ExecutionMode = MutationExecutionMode.Isolated,
        TestHostIsolationPolicy = TestHostIsolationPolicy.PerTest,
        MaxWorkers = 1,
        OutputDirectory = Path.Combine(root, "results"),
        CacheDirectory = Path.Combine(root, "cache"),
        MutationResultCacheScopeKey = "oracle-fixture-v2",
        TimeoutSeconds = 120
      });
      Assert.That(changedScope.Counters.MutationTestExecutions, Is.EqualTo(1));
      Assert.That(changedScope.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: validated-mutation-results", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, changedScope.Diagnostics));

      var resourceLocked = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productProject,
        TestProjects = [testsProject],
        Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
        TestFilter = "ProductValueIsAccepted",
        ExecutionMode = MutationExecutionMode.Isolated,
        TestHostIsolationPolicy = TestHostIsolationPolicy.PerTest,
        ResourceLocksByTestId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
          [unchangedDependency.Tests.Single().Id] = ["shared-oracle"]
        },
        MaxWorkers = 1,
        OutputDirectory = Path.Combine(root, "results"),
        CacheDirectory = Path.Combine(root, "cache"),
        MutationResultCacheScopeKey = "oracle-fixture-v1",
        TimeoutSeconds = 120
      });
      Assert.That(resourceLocked.Counters.MutationTestExecutions, Is.EqualTo(1));
      Assert.That(resourceLocked.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheBypass: validated mutation results", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, resourceLocked.Diagnostics));

      var cliAssembly = Path.Combine(Path.GetDirectoryName(typeof(MutationEngine).Assembly.Location)!, "MutationFramework.Cli.dll");
      var cliOutput = Path.Combine(root, "cli-results");
      var cliCache = Path.Combine(root, "cli-cache");
      async Task<JsonDocument> RunCliAsync()
      {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet")
        {
          WorkingDirectory = solution,
          UseShellExecute = false,
          CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
          cliAssembly, "run", "--project", productProject, "--tests", testsProject, "--framework", "net11.0",
          "--mutators", "binary-operators", "--filter", "ProductValueIsAccepted", "--workers", "1", "--timeout", "120",
          "--execution-mode", "isolated", "--test-host-isolation", "per-test", "--mutation-result-cache-scope", "cli-oracle-v1",
          "--cache", cliCache, "--output", cliOutput, "--report", "json"
        })
        {
          process.StartInfo.ArgumentList.Add(argument);
        }
        Assert.That(process.Start(), Is.True);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        Assert.That(process.ExitCode, Is.Zero);
        return JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cliOutput, "mutation-report.json")));
      }

      using var cliFirst = await RunCliAsync();
      Assert.That(cliFirst.RootElement.GetProperty("Counters").GetProperty("MutationTestExecutions").GetInt32(), Is.EqualTo(1));
      using var cliRepeated = await RunCliAsync();
      Assert.That(cliRepeated.RootElement.GetProperty("Counters").GetProperty("MutationTestExecutions").GetInt32(), Is.Zero);
      Assert.That(cliRepeated.RootElement.GetProperty("Diagnostics").EnumerateArray()
          .Any(diagnostic => diagnostic.GetString()?.StartsWith("CacheHit: validated-mutation-results", StringComparison.Ordinal) == true), Is.True);
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
  public async Task MutationDiscoveryCacheInvalidatesWhenRegisteredMutatorsChange()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenMutatorCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Product.csproj");
      var source = Path.Combine(root, "Product.cs");
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "public static class Product { public static int Evaluate(int value) => value + 1; }");
      var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
      var evaluation = new ProjectEvaluation([source], parseOptions, "net11.0", "Product", [], [project],
          new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [source] = "Product" })
      {
        ParseOptionsByFile = new Dictionary<string, CSharpParseOptions>(StringComparer.OrdinalIgnoreCase) { [source] = parseOptions }
      };
      var cache = new ArtifactCache(Path.Combine(root, "cache"));
      var firstOptions = new MutationOptions { ProjectPath = project, TestProjects = [], Registry = new MutatorRegistry([new CustomIncrementMutator()]) };
      var firstDiagnostics = new List<string>();
      var first = await Engine.DiscoverCandidatesCached([source], project, firstOptions, firstDiagnostics, CancellationToken.None, [], evaluation, cache);
      var cachedDiagnostics = new List<string>();
      var cached = await Engine.DiscoverCandidatesCached([source], project, firstOptions, cachedDiagnostics, CancellationToken.None, [], evaluation, cache);
      var differentRegistry = new MutatorRegistry([new CustomIncrementMutator(2)]);
      var changedDiagnostics = new List<string>();
      var changed = await Engine.DiscoverCandidatesCached([source], project,
          new MutationOptions { ProjectPath = project, TestProjects = [], Registry = differentRegistry }, changedDiagnostics, CancellationToken.None, [], evaluation, cache);

      Assert.That(first, Has.Count.EqualTo(1));
      Assert.That(cached.Select(candidate => candidate.Id), Is.EqualTo(first.Select(candidate => candidate.Id)));
      Assert.That(cachedDiagnostics.Any(diagnostic => diagnostic.StartsWith("CacheHit:", StringComparison.Ordinal)), Is.True);
      Assert.That(changed, Is.Empty);
      Assert.That(changedDiagnostics.Any(diagnostic => diagnostic.StartsWith("CacheMiss:", StringComparison.Ordinal) ||
          diagnostic.StartsWith("CacheInvalidated:", StringComparison.Ordinal)), Is.True);
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
  public void CacheEnvironmentFingerprintIgnoresPerInvocationAgentMetadata()
  {
    var original = Environment.GetEnvironmentVariable("CODEX_TOOL_CALL_ID");
    try
    {
      Environment.SetEnvironmentVariable("CODEX_TOOL_CALL_ID", "invocation-one");
      var first = Engine.EnvironmentFingerprint();
      Environment.SetEnvironmentVariable("CODEX_TOOL_CALL_ID", "invocation-two");

      Assert.That(Engine.EnvironmentFingerprint(), Is.EqualTo(first));
    }
    finally
    {
      Environment.SetEnvironmentVariable("CODEX_TOOL_CALL_ID", original);
    }
  }

  [Test]
  public void CacheEnvironmentFingerprintPreservesDotnetPathSelectionOrder()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenSdkPathFingerprint", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var firstRoot = Path.Combine(root, "first");
    var secondRoot = Path.Combine(root, "second");
    Directory.CreateDirectory(firstRoot);
    Directory.CreateDirectory(secondRoot);
    File.WriteAllText(Path.Combine(firstRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "first");
    File.WriteAllText(Path.Combine(secondRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "second");
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
      Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, firstRoot, secondRoot));
      var first = Engine.EnvironmentFingerprint();
      Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, secondRoot, firstRoot));

      Assert.That(Engine.EnvironmentFingerprint(), Is.Not.EqualTo(first));
    }
    finally
    {
      Environment.SetEnvironmentVariable("PATH", originalPath);
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  public async Task TestDiscoveryCacheReusesOnlyIdenticalBuiltTestArtifacts()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenTestDiscoveryCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var options = new MutationOptions
      {
        ProjectPath = Path.Combine(Root, "benchmarks", "ScaleSample", "ScaleSample", "ScaleSample.csproj"),
        TestProjects = [Path.Combine(Root, "benchmarks", "ScaleSample", "ScaleSample.Tests", "ScaleSample.Tests.csproj")],
        CacheDirectory = Path.Combine(root, "cache")
      };
      var first = await MutationEngine.DiscoverAsync(options);
      Assert.That(first.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheMiss: test-discovery", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, first.Diagnostics));
      Assert.That(first.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheBypass: compilation-artifacts", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, first.Diagnostics));

      var second = await MutationEngine.DiscoverAsync(options);
      Assert.That(second.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheHit: test-discovery", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, second.Diagnostics));
      Assert.That(second.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheBypass: compilation-artifacts", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, second.Diagnostics));
      Assert.That(second.Tests.Select(test => (test.Id, test.Name, test.ExecutorUri, test.ParameterizedCaseIdentity)),
          Is.EqualTo(first.Tests.Select(test => (test.Id, test.Name, test.ExecutorUri, test.ParameterizedCaseIdentity))));

      var filtered = await MutationEngine.DiscoverAsync(new MutationOptions
      {
        ProjectPath = options.ProjectPath,
        TestProjects = options.TestProjects,
        TestFilter = "SingleCaseCoversMutations",
        CacheDirectory = options.CacheDirectory
      });
      Assert.That(filtered.Tests, Has.Count.EqualTo(1));
      Assert.That(filtered.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheMiss: test-discovery", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, filtered.Diagnostics));
      Assert.That(filtered.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheBypass: compilation-artifacts", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, filtered.Diagnostics));
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
  public async Task CoverageMapCacheNeverSkipsBaselineExecution()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenCoverageMapCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var markerPath = Path.Combine(root, "baseline-executions.txt");
    var previousMarker = Environment.GetEnvironmentVariable("MUTAGEN_COVERAGE_BASELINE_MARKER");
    try
    {
      Environment.SetEnvironmentVariable("MUTAGEN_COVERAGE_BASELINE_MARKER", markerPath);
      var options = new MutationOptions
      {
        ProjectPath = AppProject,
        TestProjects = [TestProject],
        Mutators = new HashSet<string>(["binary-operators", "binary-patterns", "is-patterns"], StringComparer.Ordinal),
        TestFilter = "Add_ReturnsCorrectSum",
        MaxWorkers = 1,
        TimeoutSeconds = 120,
        OutputDirectory = Path.Combine(root, "output"),
        CacheDirectory = Path.Combine(root, "cache")
      };

      var first = await MutationEngine.RunAsync(options);
      var firstBaselineInvocations = File.ReadAllLines(markerPath).Length;
      var second = await MutationEngine.RunAsync(options);
      var secondBaselineInvocations = File.ReadAllLines(markerPath).Length;
      var coverageCacheDirectory = Path.Combine(options.CacheDirectory!, "coverage-maps");
      var currentCoverageKey = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(coverageCacheDirectory, "index"), "*.json").Single());
      var coverageManifest = Path.Combine(coverageCacheDirectory, currentCoverageKey + ".json");
      var corruptCoverage = JsonNode.Parse(File.ReadAllText(coverageManifest))!;
      corruptCoverage["Sites"] = null;
      File.WriteAllText(coverageManifest, corruptCoverage.ToJsonString());
      var third = await MutationEngine.RunAsync(options);
      var thirdBaselineInvocations = File.ReadAllLines(markerPath).Length;

      Assert.That(first.Counters.BaselineTestExecutions, Is.EqualTo(1));
      Assert.That(second.Counters.BaselineTestExecutions, Is.EqualTo(1));
      Assert.That(third.Counters.BaselineTestExecutions, Is.EqualTo(1));
      Assert.That(firstBaselineInvocations, Is.EqualTo(1));
      Assert.That(secondBaselineInvocations, Is.EqualTo(firstBaselineInvocations + 1),
          "The baseline-only probe environment must cause the test body to record one invocation on the second run.");
      Assert.That(thirdBaselineInvocations, Is.EqualTo(secondBaselineInvocations + 1),
          "Invalid cache records must be recomputed only after the third baseline execution.");
      Assert.That(first.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheMiss: coverage-maps", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, first.Diagnostics));
      Assert.That(second.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheHit: coverage-maps", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, second.Diagnostics));
      Assert.That(second.TestToSites, Is.EqualTo(first.TestToSites));
      Assert.That(third.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: coverage-maps", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, third.Diagnostics));
      Assert.That(third.TestToSites, Is.EqualTo(first.TestToSites));
      Assert.That(second.Mutants.Select(mutant => (mutant.Id, mutant.Status)), Is.EqualTo(first.Mutants.Select(mutant => (mutant.Id, mutant.Status))));
      Assert.That(third.Mutants.Select(mutant => (mutant.Id, mutant.Status)), Is.EqualTo(first.Mutants.Select(mutant => (mutant.Id, mutant.Status))));
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTAGEN_COVERAGE_BASELINE_MARKER", previousMarker);
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  public async Task StandaloneTestDiscoveryCacheInvalidatesWhenTestSourceChanges()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenCompilationArtifactCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var sourceRoot = Path.Combine(Root, "benchmarks", "ScaleSample");
      var copiedRoot = Path.Combine(root, "ScaleSample");
      CopyProjectSources(Path.Combine(sourceRoot, "ScaleSample"), Path.Combine(copiedRoot, "ScaleSample"));
      CopyProjectSources(Path.Combine(sourceRoot, "ScaleSample.Tests"), Path.Combine(copiedRoot, "ScaleSample.Tests"));
      var productionProject = Path.Combine(copiedRoot, "ScaleSample", "ScaleSample.csproj");
      var testProject = Path.Combine(copiedRoot, "ScaleSample.Tests", "ScaleSample.Tests.csproj");
      var options = new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        CacheDirectory = Path.Combine(root, "cache")
      };

      var first = await MutationEngine.DiscoverAsync(options);
      Assert.That(first.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheMiss: test-discovery", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, first.Diagnostics));
      Assert.That(first.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheBypass: compilation-artifacts", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, first.Diagnostics));

      var second = await MutationEngine.DiscoverAsync(options);
      Assert.That(second.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheHit: test-discovery", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, second.Diagnostics));
      Assert.That(second.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheBypass: compilation-artifacts", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, second.Diagnostics));

      var changedSource = Path.Combine(copiedRoot, "ScaleSample.Tests", "ThresholdCoverageTests.cs");
      File.AppendAllText(changedSource, Environment.NewLine + "// compilation cache invalidation fixture");
      var changed = await MutationEngine.DiscoverAsync(options);
      Assert.That(changed.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: test-discovery", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, changed.Diagnostics));
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
  public async Task ProjectEvaluationCacheTracksProjectAndSourceInputs()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenProjectEvaluationCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      File.WriteAllText(project, "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Fixture.cs\" /></ItemGroup></Project>");
      File.WriteAllText(source, "public class Fixture { public int Value => 1 + 2; }");
      var cache = new ArtifactCache(Path.Combine(root, ".mutagen-cache"));
      var metrics = new RunMetrics();
      var diagnostics = new List<string>();
      var first = await Engine.GetCompileFiles(project, metrics, CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(first.CompileFiles, Does.Contain(source));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheMiss: project-evaluation", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));
      diagnostics.Clear();
      await Engine.GetCompileFiles(project, metrics, CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: project-evaluation", StringComparison.Ordinal)), Is.True);
      File.WriteAllText(project, "<Project><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>Fixture</AssemblyName><DefineConstants>FEATURE_CHANGED</DefineConstants></PropertyGroup><ItemGroup><Compile Include=\"Fixture.cs\" /></ItemGroup></Project>");
      diagnostics.Clear();
      var changed = await Engine.GetCompileFiles(project, metrics, CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(changed.ParseOptions.PreprocessorSymbolNames, Does.Contain("FEATURE_CHANGED"));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheInvalidated: project-evaluation", StringComparison.Ordinal)), Is.True);
      var options = new MutationOptions { ProjectPath = project, TestProjects = [] };
      var unsupported = new List<MutationResult>();
      diagnostics.Clear();
      await Engine.DiscoverCandidatesCached(changed.CompileFiles, project, options, diagnostics, CancellationToken.None, unsupported, changed, cache);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheMiss: mutation-discovery", StringComparison.Ordinal)), Is.True);
      diagnostics.Clear();
      await Engine.DiscoverCandidatesCached(changed.CompileFiles, project, options, diagnostics, CancellationToken.None, unsupported, changed, cache);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: mutation-discovery", StringComparison.Ordinal)), Is.True);
      File.WriteAllText(source, "public class Fixture { public int Value => 3 + 4; }");
      diagnostics.Clear();
      var changedSources = await Engine.GetCompileFiles(project, metrics, CancellationToken.None, cache: cache, diagnostics: diagnostics);
      diagnostics.Clear();
      await Engine.DiscoverCandidatesCached(changedSources.CompileFiles, project, options, diagnostics, CancellationToken.None, unsupported, changedSources, cache);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheInvalidated: mutation-discovery", StringComparison.Ordinal)), Is.True);
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
  public async Task ProjectFrameworkSelectionCacheTracksImportedProjectConfiguration()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenFrameworkSelectionCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      File.WriteAllText(project,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net11.0</TargetFrameworks></PropertyGroup></Project>");
      File.WriteAllText(source, "public class Fixture { public int Value => 1 + 2; }");
      var cache = new ArtifactCache(Path.Combine(root, "cache"));
      var diagnostics = new List<string>();

      var first = await Engine.GetProjectGraph(project, new RunMetrics(), CancellationToken.None, "net11.0", cache, diagnostics);
      Assert.That(first.TargetFramework, Is.EqualTo("net11.0"));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheMiss: project-framework-selection", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));

      diagnostics.Clear();
      var repeated = await Engine.GetProjectGraph(project, new RunMetrics(), CancellationToken.None, "net11.0", cache, diagnostics);
      Assert.That(repeated.TargetFramework, Is.EqualTo("net11.0"));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: project-framework-selection", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));

      File.WriteAllText(project,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>");
      diagnostics.Clear();
      var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
          await Engine.GetProjectGraph(project, new RunMetrics(), CancellationToken.None, "net11.0", cache, diagnostics));
      Assert.That(exception!.Message, Does.Contain("not supported"));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheInvalidated: project-framework-selection", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));
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
  public async Task ProjectEvaluationUsesCentralPackageManagementAndInvalidatesItsCache()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenCentralPackages", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      var packages = Path.Combine(root, "Directory.Packages.props");
      File.WriteAllText(project,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" /></ItemGroup></Project>");
      File.WriteAllText(source, "public class Fixture { public int Value => 1 + 2; }");
      File.WriteAllText(packages,
          "<Project><ItemGroup><PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>");

      var cache = new ArtifactCache(Path.Combine(root, ".mutagen-cache"));
      var diagnostics = new List<string>();
      var first = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);

      Assert.That(first.CompileFiles, Does.Contain(source));
      Assert.That(first.BuildInputs, Does.Contain(packages));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheMiss: project-evaluation", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));

      diagnostics.Clear();
      await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: project-evaluation", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));

      File.WriteAllText(packages,
          "<Project><ItemGroup><PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.4\" /></ItemGroup></Project>");
      diagnostics.Clear();
      var changed = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);

      Assert.That(changed.BuildInputs, Does.Contain(packages));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheInvalidated: project-evaluation", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));
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
  public async Task ProjectEvaluationDoesNotReuseCacheWithWildcardImportedProps()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenWildcardImportCache", Guid.NewGuid().ToString("N"));
    var projectDirectory = Path.Combine(root, "Project");
    var sharedDirectory = Path.Combine(root, "SharedProps");
    Directory.CreateDirectory(projectDirectory);
    Directory.CreateDirectory(sharedDirectory);
    try
    {
      var project = Path.Combine(projectDirectory, "Fixture.csproj");
      var source = Path.Combine(projectDirectory, "Fixture.cs");
      var importedProps = Path.Combine(sharedDirectory, "Assembly.props");
      File.WriteAllText(project,
          "<Project><Import Project=\"../SharedProps/*.props\" /><PropertyGroup><TargetFramework>net11.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Fixture.cs\" /></ItemGroup></Project>");
      File.WriteAllText(source, "public sealed class Fixture { }");
      File.WriteAllText(importedProps, "<Project><PropertyGroup><AssemblyName>Before</AssemblyName></PropertyGroup></Project>");
      var cache = new ArtifactCache(Path.Combine(root, ".mutagen-cache"));
      var diagnostics = new List<string>();
      var first = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(first.AssemblyName, Is.EqualTo("Before"));

      File.WriteAllText(importedProps, "<Project><PropertyGroup><AssemblyName>After</AssemblyName></PropertyGroup></Project>");
      diagnostics.Clear();
      var changed = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);

      Assert.That(changed.AssemblyName, Is.EqualTo("After"));
      Assert.That(diagnostics.Any(message => message.Contains("wildcard", StringComparison.OrdinalIgnoreCase) &&
          message.StartsWith("CacheBypass:", StringComparison.Ordinal)), Is.True, string.Join(Environment.NewLine, diagnostics));
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
      var evaluation = await Engine.GetProjectGraph(appProject, new RunMetrics(), CancellationToken.None, null, cache, diagnostics);
      Assert.That(evaluation.CompileFiles, Does.Contain(sharedSource));
      Assert.That(evaluation.CompileFiles, Does.Contain(appSource));
      Assert.That(evaluation.AssemblyNamesByFile[sharedSource], Is.EqualTo("Shared.Product"));
      Assert.That(evaluation.AssemblyNamesByFile[appSource], Is.EqualTo("Application.Product"));
      Assert.That(evaluation.ParseOptionsByFile[sharedSource].PreprocessorSymbolNames, Does.Contain("SHARED_FEATURE"));
      Assert.That(evaluation.ParseOptionsByFile[appSource].PreprocessorSymbolNames, Does.Contain("APP_FEATURE"));
      Assert.That(evaluation.ProjectPaths, Has.Length.EqualTo(2));
      var candidates = Engine.DiscoverCandidates(evaluation.CompileFiles, appProject,
          new MutationOptions { ProjectPath = appProject, TestProjects = [] }, [], CancellationToken.None,
          evaluatedParseOptions: evaluation.ParseOptions, evaluatedTargetFramework: evaluation.TargetFramework,
          assemblyIdentity: evaluation.AssemblyName, assemblyNamesByFile: evaluation.AssemblyNamesByFile,
          parseOptionsByFile: evaluation.ParseOptionsByFile);
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
      var evaluation = await Engine.GetProductionGraph(firstProject, [secondProject], new RunMetrics(), CancellationToken.None,
          null, new ArtifactCache(Path.Combine(root, ".mutagen-cache")), []);
      Assert.That(evaluation.CompileFiles, Does.Contain(firstSource));
      Assert.That(evaluation.CompileFiles, Does.Contain(secondSource));
      Assert.That(evaluation.AssemblyNamesByFile[firstSource], Is.EqualTo("First.Product"));
      Assert.That(evaluation.AssemblyNamesByFile[secondSource], Is.EqualTo("Second.Product"));
      var candidates = Engine.DiscoverCandidates(evaluation.CompileFiles, firstProject,
          new MutationOptions { ProjectPath = firstProject, TestProjects = [] }, [], CancellationToken.None,
          evaluatedParseOptions: evaluation.ParseOptions, evaluatedTargetFramework: evaluation.TargetFramework,
          assemblyIdentity: evaluation.AssemblyName, assemblyNamesByFile: evaluation.AssemblyNamesByFile);
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
  public void MutatorSelectionChangesGeneratedCandidatesWithoutChangingTheSchemaBuilder()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenMutatorFixture", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var source = Path.Combine(root, "Fixture.cs");
      var project = Path.Combine(root, "Fixture.csproj");
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "public static class Fixture { public static bool Evaluate(int x) => x > 0 && true || false; public static int Mod(int x) => x % 2; }");

      var options = new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [],
        Mutators = new HashSet<string>(["boolean-literals"], StringComparer.Ordinal)
      };
      var booleanCandidates = Engine.DiscoverCandidates([source], project, options, [], CancellationToken.None);

      Assert.That(booleanCandidates, Has.Count.EqualTo(2));
      Assert.That(booleanCandidates.Select(candidate => (candidate.Original, candidate.Replacement)),
          Is.EquivalentTo(new[] { ("true", "false"), ("false", "true") }));
      Assert.That(booleanCandidates.All(candidate => candidate.Operator == "Boolean"), Is.True);

      var target = Path.Combine(root, "instrumented", "Fixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, booleanCandidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static bool IsActiveAndHit(int siteId, int id) { Hit(siteId); return IsActive(id); } public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target),
        CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs")
      };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("BooleanLiteralFixture_" + Guid.NewGuid().ToString("N"), trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var compileErrors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(compileErrors, Is.Empty, string.Join(Environment.NewLine, compileErrors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("Fixture")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      var evaluate = fixture.GetMethod("Evaluate")!;
      var trueMutation = booleanCandidates.Single(candidate => candidate.Original == "true");
      var falseMutation = booleanCandidates.Single(candidate => candidate.Original == "false");
      active.SetValue(null, -1);
      Assert.That(evaluate.Invoke(null, [1]), Is.EqualTo(true));
      Assert.That(evaluate.Invoke(null, [-1]), Is.EqualTo(false));
      active.SetValue(null, int.Parse(trueMutation.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(evaluate.Invoke(null, [1]), Is.EqualTo(false));
      active.SetValue(null, int.Parse(falseMutation.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(evaluate.Invoke(null, [-1]), Is.EqualTo(true));
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
  public void RegisteredExtensionMutatorUsesGenericSchemaWithoutEnginePipelineChanges()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenCustomMutator", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Extension.csproj");
      var source = Path.Combine(root, "Extension.cs");
      var target = Path.Combine(root, "instrumented", "Extension.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "public static class ExtensionFixture { public static int Evaluate(int value) => value + 1; }");
      var registry = new MutatorRegistry([new CustomIncrementMutator()]);
      var options = new MutationOptions { ProjectPath = project, TestProjects = [], Registry = registry };
      var candidates = Engine.DiscoverCandidates([source], project, options, [], CancellationToken.None);

      Assert.That(candidates, Has.Count.EqualTo(1));
      Assert.That(candidates[0].ApplicationKind, Is.EqualTo(MutationApplicationKind.ExpressionReplacement));
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static bool IsActiveAndHit(int siteId, int id) { Hit(siteId); return IsActive(id); } public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("CustomMutatorFixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("ExtensionFixture")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      Assert.That(fixture.GetMethod("Evaluate")!.Invoke(null, [7]), Is.EqualTo(8));
      active.SetValue(null, int.Parse(candidates[0].MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("Evaluate")!.Invoke(null, [7]), Is.EqualTo(6));
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
          Engine.RewriteSources(new Dictionary<string, string> { [original] = escapedTarget }, [], root, workspace));

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

      var evaluation = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, "net11.0");
      var consumerEvaluation = await Engine.GetCompileFiles(consumerProject, new RunMetrics(), CancellationToken.None, "net11.0");
      var linkedItems = evaluation.LinkedCompileItems.Concat(consumerEvaluation.LinkedCompileItems).ToArray();
      var linkedItem = linkedItems.First();
      Assert.That(linkedItem.File, Is.EqualTo(source));

      var mapped = Engine.MaterializeLinkedSources(workspace, solution, linkedItems);

      Assert.That(mapped[source], Does.StartWith(workspace));
      Assert.That(File.ReadAllText(mapped[source]), Is.EqualTo(sourceText));
      var remappedProject = XDocument.Load(copiedProject);
      var includes = remappedProject.Descendants().Where(element => element.Name.LocalName == "Compile" && element.Attribute("Include") is not null)
          .Select(element => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(copiedProject)!, element.Attribute("Include")!.Value))).ToArray();
      Assert.That(includes, Does.Contain(mapped[source]).And.Contain(mapped[otherSource]));
      Assert.That(File.ReadAllText(Path.Combine(workspace, "Directory.Build.props")), Does.Contain("$(SharedSourcePattern)"));
      Assert.That(File.ReadAllText(mapped[otherSource]), Is.EqualTo(otherSourceText));
      Assert.That(File.ReadAllText(source), Is.EqualTo(sourceText));
      var remappedEvaluation = await Engine.GetCompileFiles(copiedProject, new RunMetrics(), CancellationToken.None, "net11.0");
      Assert.That(remappedEvaluation.CompileFiles, Does.Contain(mapped[source]));
      Assert.That(remappedEvaluation.CompileFiles, Does.Contain(mapped[otherSource]));
      var consumerEvaluationAfterRemap = await Engine.GetCompileFiles(consumerProject.Replace(solution, workspace, StringComparison.Ordinal),
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
      Assert.That(mutationCompilationManifest["Files"]!.AsArray()
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
      mutationCompilationManifest["Files"]![0]!["RelativePath"] = "../escaped-output.dll";
      File.WriteAllText(mutationManifestPath, mutationCompilationManifest.ToJsonString());
      var unsafeManifest = await MutationEngine.RunAsync(options);
      Assert.That(unsafeManifest.Diagnostics.Any(diagnostic => diagnostic.StartsWith("CacheInvalidated: mutation-compilation-artifacts manifest contains an unsafe path", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, unsafeManifest.Diagnostics));
      Assert.That(unsafeManifest.Mutants.Single().Status, Is.EqualTo(MutationStatus.Killed));

      mutationCompilationKey = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(mutationCacheDirectory, "index"), "*.json").Single());
      mutationManifestPath = Path.Combine(mutationCacheDirectory, mutationCompilationKey + ".json");
      mutationCompilationManifest = JsonNode.Parse(File.ReadAllText(mutationManifestPath))!;
      var mutationManifestFiles = mutationCompilationManifest["Files"]!.AsArray();
      var originalMutationBlob = mutationManifestFiles[0]!["BlobKey"]!.GetValue<string>();
      var alternateMutationBlob = mutationManifestFiles.Select(file => file!["BlobKey"]!.GetValue<string>())
          .First(blob => !string.Equals(blob, originalMutationBlob, StringComparison.Ordinal));
      mutationManifestFiles[0]!["BlobKey"] = alternateMutationBlob;
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
  public void MutationIdentityIncludesAssemblyAndTargetFramework()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenStableIdentity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var source = Path.Combine(root, "Shared.cs");
      var project = Path.Combine(root, "Product.csproj");
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "public static class Shared { public static int Add(int a, int b) => a + b; }");
      var first = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, assemblyIdentity: "ProductA");
      var second = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, assemblyIdentity: "ProductB");
      Assert.That(first.Select(candidate => candidate.Id), Is.Not.EqualTo(second.Select(candidate => candidate.Id)));
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
  public async Task AssignmentOperatorMutationsHaveCompleteCompileableGenerationAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenAssignmentParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Assignment.csproj");
      var source = Path.Combine(root, "Assignment.cs");
      var target = Path.Combine(root, "instrumented", "Assignment.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      var operations = new (string Name, string Operator)[]
      {
        ("Add", "+="), ("Subtract", "-="), ("Multiply", "*="), ("Divide", "/="), ("Modulo", "%="),
        ("And", "&="), ("Or", "|="), ("Xor", "^="), ("LeftShift", "<<="), ("RightShift", ">>="), ("UnsignedRightShift", ">>>=")
      };
      File.WriteAllText(source, "public static class Assignment { " +
          string.Join(" ", operations.Select(operation => $"public static int {operation.Name}(int a, int b) {{ a {operation.Operator} b; return a; }}")) +
          " public static int? Coalesce(int? a, int? b) { a ??= b; return a; } }");
      var candidates = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None);
      Assert.That(candidates.Count(candidate => candidate.Operator == nameof(MutationCategory.Assignment)), Is.EqualTo(18));
      using (var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json"))))
      {
        var documented = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Assignment Statements (_assignment_)" &&
                row.GetProperty("status").GetString() == "supported")
            .Select(row => (From: row.GetProperty("from").GetString()!, To: row.GetProperty("to").GetString()!)).ToArray();
        Assert.That(documented, Has.Length.EqualTo(14));
        foreach (var transformation in documented)
        {
          Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.Assignment) &&
              candidate.Original.Contains(transformation.From, StringComparison.Ordinal) &&
              candidate.ReplacementToken == transformation.To), Is.True,
              $"Missing documented assignment transformation: {transformation.From} -> {transformation.To}");
        }
      }
      Assert.That(candidates.Select(candidate => candidate.ReplacementToken), Does.Contain("<<="));
      Assert.That(candidates.Select(candidate => candidate.ReplacementToken), Does.Contain(">>>="));
      Assert.That(candidates.Select(candidate => candidate.ReplacementToken), Does.Contain("="));
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("AssignmentFixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      Assert.That(compilation.Emit(bytes).Success, Is.True);
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("Assignment")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      foreach (var candidate in candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Assignment)))
      {
        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var method = candidate.Original.Contains("??=", StringComparison.Ordinal)
            ? "Coalesce"
            : operations.Single(operation => operation.Operator == candidate.Original.Split(' ')[1]).Name;
        Assert.DoesNotThrow(() => fixture.GetMethod(method)!.Invoke(null, candidate.Original.Contains("??=", StringComparison.Ordinal) ? [3, 1] : [7, 1]), candidate.Replacement);
      }
      var coalescing = candidates.Single(candidate => candidate.Original == "a ??= b");
      active.SetValue(null, int.Parse(coalescing.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("Coalesce")!.Invoke(null, [3, 2]), Is.EqualTo(2));

      var engineAssertions = new List<string>();
      var integerInputs = (from a in Enumerable.Range(-24, 49)
                           from b in new[] { -7, -3, -2, -1, 1, 2, 3, 7, 16 }
                           select (A: a, B: b)).ToArray();
      foreach (var operation in operations)
      {
        foreach (var (a, b) in integerInputs)
        {
          active.SetValue(null, -1);
          var expected = (int)fixture.GetMethod(operation.Name)!.Invoke(null, [a, b])!;
          engineAssertions.Add($"Assert.That(Assignment.{operation.Name}({a}, {b}), Is.EqualTo({expected}));");
        }
      }
      var nullableInputs = (from a in new int?[] { null, -1, 0, 1, 3 }
                            from b in new int?[] { null, -2, 0, 2, 5 }
                            select (A: a, B: b)).ToArray();
      foreach (var (a, b) in nullableInputs)
      {
        active.SetValue(null, -1);
        var expected = fixture.GetMethod("Coalesce")!.Invoke(null, [a, b]);
        engineAssertions.Add($"Assert.That(Assignment.Coalesce({a?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}, {b?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}), Is.EqualTo({expected?.ToString() ?? "null"}));");
      }
      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "Assignment.csproj");
      var testProject = Path.Combine(testsDirectory, "Assignment.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AssemblyName>Assignment.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "Assignment.cs"), File.ReadAllText(source));
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\Assignment.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "AssignmentConformanceTests.cs"),
          "using NUnit.Framework; [TestFixture] public sealed class AssignmentConformanceTests { [Test] public void EveryAssignmentMutationIsCheckedAgainstOriginalBehavior() { " +
          string.Join(" ", engineAssertions) + " } }");
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["assignment-expressions"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      var assignmentCandidates = candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Assignment)).ToArray();
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(assignmentCandidates.Length));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "Assignment.cs" && mutant.Line == 1 && mutant.Column > 0), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => assignmentCandidates.Any(candidate =>
          candidate.Original == mutant.Original && candidate.Replacement == mutant.Replacement)), Is.True);
      Assert.That(engineResult.Counters.CoveredMutants, Is.EqualTo(assignmentCandidates.Length));
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
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
  public async Task InitializerMutationsCompileActivateAndKillInEngineAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenInitializerParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      var target = Path.Combine(root, "instrumented", "Fixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "using System; using System.Collections.Generic; using System.Collections.ObjectModel; public sealed class SomeClass { public string Foo { get; set; } = string.Empty; } public static class Fixture { public static int[] Array() => new int[] { 1, 2 }; public static int[] VariableArray() { int[] numbers = { 1, 2 }; return numbers; } public static List<int> List() => new List<int> { 1, 2 }; public static Collection<int> LegacyCollection() => new Collection<int> { 1, 2 }; public static Dictionary<int, int> Map() => new Dictionary<int, int> { { 1, 1 } }; public static SomeClass Object() => new SomeClass { Foo = \"Bar\" }; public static int[] Collection() => [1, 2]; public static int StackArray() { Span<int> values = stackalloc int[] { 1, 2 }; return values.Length == 2 ? values[0] + values[1] : 0; } public static int StackArraySized() { Span<int> values = stackalloc int[2]; return values.Length; } }");
      var unsupported = new List<MutationResult>();
      var candidates = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported);
      var initializers = candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Initialization)).ToArray();
      Assert.That(initializers, Has.Length.EqualTo(8), string.Join(Environment.NewLine, unsupported.Select(mutation => $"{mutation.SourceFile}:{mutation.Line}: {mutation.Original} -> {mutation.Replacement}: {mutation.Diagnostic}")));
      var stackAllocation = initializers.SingleOrDefault(candidate => candidate.Original.Contains("stackalloc", StringComparison.Ordinal));
      Assert.That(stackAllocation, Is.Not.Null, "The pinned Stryker.NET ArrayCreationMutator also clears initialized stackalloc expressions.");
      Assert.That(stackAllocation!.Replacement, Does.Contain("stackalloc int[] { }"));
      Assert.That(stackAllocation.ApplicationKind, Is.EqualTo(MutationApplicationKind.ExpressionReplacement));
      Assert.That(stackAllocation.UseInlineSiteProbe, Is.True, "Ref-like stackalloc expressions must use inline activation, not the generic probe delegate.");
      Assert.That(initializers.Any(candidate => candidate.Original.Contains("stackalloc int[2]", StringComparison.Ordinal)), Is.False,
          "Stack allocations with an explicit size and no initializer are not eligible for initializer removal.");
      using (var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json"))))
      {
        var transformations = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Initialization (_initializer_)" &&
                row.GetProperty("status").GetString() == "supported")
            .Select(row => (From: row.GetProperty("from").GetString()!.TrimEnd(';'), To: row.GetProperty("to").GetString()!.TrimEnd(';'))).ToArray();
        Assert.That(transformations, Has.Length.EqualTo(6));
        var sourceDerived = parity.RootElement.GetProperty("sourceDerivedCases").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Initialization (_initializer_)" &&
                row.GetProperty("status").GetString() == "supported")
            .ToArray();
        Assert.That(sourceDerived, Has.Length.EqualTo(1));
        Assert.That(sourceDerived[0].GetProperty("source").GetString(), Does.EndWith("ArrayCreationMutator.cs"));
        Assert.That(sourceDerived[0].GetProperty("from").GetString(), Is.EqualTo("stackalloc int[] { 1, 2 }"));
        Assert.That(sourceDerived[0].GetProperty("to").GetString(), Is.EqualTo("stackalloc int[] { }"));
        static string NormalizeInitializer(string value) => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
        foreach (var transformation in transformations)
        {
          var equivalentOriginal = transformation.From.StartsWith("int[] numbers =", StringComparison.Ordinal)
              ? transformation.From["int[] ".Length..]
              : transformation.From;
          var equivalentReplacement = transformation.From.StartsWith("int[] numbers =", StringComparison.Ordinal)
              ? "new int[] { }"
              : transformation.To;
          Assert.That(initializers.Any(candidate => NormalizeInitializer(candidate.Original) == NormalizeInitializer(equivalentOriginal) &&
              NormalizeInitializer(candidate.Replacement) == NormalizeInitializer(equivalentReplacement)), Is.True,
              $"Missing documented initializer transformation: {transformation.From} -> {transformation.To}. Candidates: " +
              string.Join(" | ", initializers.Select(candidate => $"{candidate.Original} -> {candidate.Replacement}")));
        }
        Assert.That(initializers.Any(candidate => NormalizeInitializer(candidate.Original) == NormalizeInitializer(sourceDerived[0].GetProperty("from").GetString()!) &&
            NormalizeInitializer(candidate.Replacement) == NormalizeInitializer(sourceDerived[0].GetProperty("to").GetString()!)), Is.True,
            "Missing source-derived Stryker initializer transformation for stackalloc.");
      }
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static bool IsActiveAndHit(int siteId, int id) { Hit(siteId); return IsActive(id); } public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("InitializerFixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("Fixture")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      Assert.That(((int[])fixture.GetMethod("Array")!.Invoke(null, null)!).Length, Is.EqualTo(2));
      Assert.That(((int[])fixture.GetMethod("VariableArray")!.Invoke(null, null)!).Length, Is.EqualTo(2));
      Assert.That(((List<int>)fixture.GetMethod("List")!.Invoke(null, null)!).Count, Is.EqualTo(2));
      Assert.That(((System.Collections.ObjectModel.Collection<int>)fixture.GetMethod("LegacyCollection")!.Invoke(null, null)!).Count, Is.EqualTo(2));
      Assert.That(((Dictionary<int, int>)fixture.GetMethod("Map")!.Invoke(null, null)!).Count, Is.EqualTo(1));
      Assert.That((string)fixture.GetMethod("Object")!.Invoke(null, null)!.GetType().GetProperty("Foo")!.GetValue(fixture.GetMethod("Object")!.Invoke(null, null))!, Is.EqualTo("Bar"));
      Assert.That(((int[])fixture.GetMethod("Collection")!.Invoke(null, null)!).Length, Is.EqualTo(2));
      Assert.That(fixture.GetMethod("StackArray")!.Invoke(null, null), Is.EqualTo(3));
      foreach (var candidate in initializers)
      {
        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var result = candidate.Original.StartsWith("numbers =", StringComparison.Ordinal)
            ? fixture.GetMethod("VariableArray")!.Invoke(null, null)
            : candidate.Original.StartsWith("new int[]", StringComparison.Ordinal) ? fixture.GetMethod("Array")!.Invoke(null, null)
            : candidate.Original.StartsWith("new List", StringComparison.Ordinal) ? fixture.GetMethod("List")!.Invoke(null, null)
            : candidate.Original.StartsWith("new Collection", StringComparison.Ordinal) ? fixture.GetMethod("LegacyCollection")!.Invoke(null, null)
            : candidate.Original.StartsWith("new Dictionary", StringComparison.Ordinal) ? fixture.GetMethod("Map")!.Invoke(null, null)
            : candidate.Original.StartsWith("new SomeClass", StringComparison.Ordinal) ? fixture.GetMethod("Object")!.Invoke(null, null)
            : candidate.Original.Contains("stackalloc", StringComparison.Ordinal) ? fixture.GetMethod("StackArray")!.Invoke(null, null)
            : fixture.GetMethod("Collection")!.Invoke(null, null);
        Assert.That(result, Is.Not.Null);
        Assert.That(result switch { int[] array => array.Length, List<int> list => list.Count, System.Collections.ObjectModel.Collection<int> collection => collection.Count, Dictionary<int, int> dictionary => dictionary.Count, int value => value, object item => string.IsNullOrEmpty((string?)item.GetType().GetProperty("Foo")!.GetValue(item)) ? 0 : 1, null => -1 }, Is.Zero,
            $"Initializer mutant did not activate: {candidate.Original} -> {candidate.Replacement}");
      }

      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "Fixture.csproj");
      var testProject = Path.Combine(testsDirectory, "Fixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "Fixture.cs"), File.ReadAllText(source));
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\Fixture.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "InitializerConformanceTests.cs"),
          "using NUnit.Framework; using System.Collections.Generic; using System.Collections.ObjectModel; [TestFixture] public sealed class InitializerConformanceTests { [Test] public void EveryInitializerMutationIsCheckedAgainstOriginalBehavior() { Assert.That(Fixture.Array(), Is.EqualTo(new[] { 1, 2 })); Assert.That(Fixture.VariableArray(), Is.EqualTo(new[] { 1, 2 })); Assert.That(Fixture.List(), Is.EqualTo(new List<int> { 1, 2 })); Assert.That(Fixture.LegacyCollection(), Is.EqualTo(new Collection<int> { 1, 2 })); Assert.That(Fixture.Map(), Is.EqualTo(new Dictionary<int, int> { { 1, 1 } })); Assert.That(Fixture.Object().Foo, Is.EqualTo(\"Bar\")); Assert.That(Fixture.Collection(), Is.EqualTo(new[] { 1, 2 })); Assert.That(Fixture.StackArray(), Is.EqualTo(3)); } }");
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["initialization"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(initializers.Length));
      Assert.That(engineResult.Counters.CoveredMutants, Is.EqualTo(initializers.Length));
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "Fixture.cs" && mutant.Line == 1 && mutant.Column > 0), Is.True);
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
  public async Task StatementRemovalMutationsCompileActivateAndExecuteInEngineAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenRemovalParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      var target = Path.Combine(root, "instrumented", "Fixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "using System; using System.Collections.Generic; public static class Fixture { public static int Calls; public static int NestedCalls; public static int ReturnValue(int value) { return value; } public static int ThrowValue() { throw new InvalidOperationException(); } public static int ThrowException(Exception exception) { throw exception; } public static int Rethrow() { try { throw new InvalidOperationException(); } catch { throw; } } public static int BreakLoop() { int i = 0; while (i++ < 2) { break; i++; } return i; } public static int ContinueLoop() { int sum = 0; for (int i = 0; i < 3; i++) { if (i == 1) continue; sum += i; } return sum; } public static int GotoTarget() { goto Target; return 1; Target: return 2; } public static void ReturnVoid() { return; Calls++; } public static void CallStatement() { MyMethodCall(); } public static void Nested(bool condition) { if (condition) { NestedCalls++; } } public static void InfiniteLoopBlock() { while (true) { break; } } public static void SwitchBlock(int value) { switch (value) { case 0: { Calls++; break; } } } private static void MyMethodCall() { Calls++; } public static IEnumerable<int> YieldValue() { yield return 1; yield return 2; } public static IEnumerable<int> YieldStop() { yield return 1; yield break; yield return 2; } public struct AssignmentConstructor { public int Value; public AssignmentConstructor(int value) { Value = value; } } }");
      var unsupported = new List<MutationResult>();
      var candidates = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported)
          .Where(candidate => candidate.Operator == nameof(MutationCategory.Removal)).ToArray();
      Assert.That(unsupported.Any(mutation => mutation.Operator == nameof(MutationCategory.Removal) &&
          mutation.Diagnostic?.Contains("exception-handling block", StringComparison.Ordinal) == true), Is.True,
          "Blocks that cannot be emptied without breaking method completion must be reported as unsupported.");
      using (var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json"))))
      {
        var transformations = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Removal mutators (_statement_, _block_)" &&
                row.GetProperty("status").GetString() == "supported")
            .Select(row => row.GetProperty("from").GetString()!).ToArray();
        Assert.That(transformations, Has.Length.EqualTo(12));
        foreach (var transformation in transformations)
        {
          var matches = transformation switch
          {
            "void Function() { Age++; }" => candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("return; Calls++;", StringComparison.Ordinal)),
            "int Function() { Age++; return Age; }" => candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("return value;", StringComparison.Ordinal)),
            "return;" => candidates.Any(candidate => candidate.Original == "return;" && candidate.ReplacementToken == "removal:ReturnStatement"),
            "return value;" => candidates.Any(candidate => candidate.Original == "return value;" && candidate.ReplacementToken == "removal:ReturnStatement"),
            "break;" => candidates.Any(candidate => candidate.Original == "break;" && candidate.ReplacementToken == "removal:BreakStatement"),
            "continue;" => candidates.Any(candidate => candidate.Original == "continue;" && candidate.ReplacementToken == "removal:ContinueStatement"),
            "goto;" => candidates.Any(candidate => candidate.Original.StartsWith("goto ", StringComparison.Ordinal) && candidate.ReplacementToken == "removal:GotoStatement"),
            "throw;" => candidates.Any(candidate => candidate.Original == "throw;" && candidate.ReplacementToken == "removal:ThrowStatement"),
            "throw exception;" => candidates.Any(candidate => candidate.Original == "throw exception;" && candidate.ReplacementToken == "removal:ThrowStatement"),
            "yield return value;" => candidates.Any(candidate => candidate.Original.StartsWith("yield return", StringComparison.Ordinal) && candidate.ReplacementToken == "removal:YieldReturnStatement"),
            "yield break;" => candidates.Any(candidate => candidate.Original == "yield break;" && candidate.ReplacementToken == "removal:YieldBreakStatement"),
            "MyMethodCall();" => candidates.Any(candidate => candidate.Original == "MyMethodCall();" && candidate.ReplacementToken == "removal:ExpressionStatement"),
            _ => throw new AssertionException($"No removal fixture mapping for documented transformation: {transformation}")
          };
          Assert.That(matches, Is.True, $"No candidate for documented removal transformation: {transformation}");
        }
        var sourceDerived = parity.RootElement.GetProperty("sourceDerivedCases").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Removal mutators (_statement_, _block_)" &&
                row.GetProperty("status").GetString() == "supported")
            .ToArray();
        Assert.That(sourceDerived, Has.Length.EqualTo(1));
        Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "removal:block" &&
            NormalizeRemoval(candidate.Original) == NormalizeRemoval(sourceDerived[0].GetProperty("from").GetString()!) &&
            NormalizeRemoval(candidate.Replacement) == NormalizeRemoval(sourceDerived[0].GetProperty("to").GetString()!)), Is.True,
            "Missing the pinned BlockMutator's implementation-derived nested-block transformation.");
      }
      static string NormalizeRemoval(string value) => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
      Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original == "{ break; }"), Is.False,
          "Stryker excludes blocks that are directly inside an infinite while loop.");
      Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original == "{ Calls++; break; }"), Is.False,
          "Stryker excludes blocks inside switch sections.");
      Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original == "{ Value = value; }"), Is.False,
          "Stryker protects assignments in struct constructors.");
      Assert.That(candidates.Select(candidate => candidate.ReplacementToken).Distinct(StringComparer.Ordinal),
          Does.Contain("removal:ReturnStatement").And.Contain("removal:BreakStatement").And.Contain("removal:ContinueStatement")
              .And.Contain("removal:GotoStatement").And.Contain("removal:ThrowStatement").And.Contain("removal:YieldReturnStatement")
              .And.Contain("removal:YieldBreakStatement").And.Contain("removal:ExpressionStatement").And.Contain("removal:block"));
      var breakCandidates = candidates.Where(candidate => candidate.Original == "break;").ToArray();
      Assert.That(breakCandidates, Has.Length.EqualTo(2), string.Join(Environment.NewLine, breakCandidates.Select(candidate =>
      {
        var node = CSharpSyntaxTree.ParseText(File.ReadAllText(source)).GetRoot()
            .FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(candidate.Start, candidate.Length));
        return $"At {candidate.Start}: {string.Join(" > ", node.AncestorsAndSelf().Select(ancestor => ancestor.Kind()))}";
      })));
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates.ToList(), root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("RemovalFixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())) + Environment.NewLine + File.ReadAllText(target));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("Fixture")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      object? Invoke(string name, params object[] args) => fixture.GetMethod(name)!.Invoke(null, args);
      Assert.That(Invoke("ReturnValue", 7), Is.EqualTo(7));
      Assert.That(Invoke("BreakLoop"), Is.EqualTo(1));
      Assert.That(Invoke("ContinueLoop"), Is.EqualTo(2));
      Assert.That(Invoke("GotoTarget"), Is.EqualTo(2));
      Invoke("InfiniteLoopBlock");
      fixture.GetField("Calls")!.SetValue(null, 0);
      Invoke("SwitchBlock", 0);
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.EqualTo(1));
      Assert.Throws<System.Reflection.TargetInvocationException>(() => Invoke("ThrowValue"));
      Assert.That(((IEnumerable<int>)Invoke("YieldValue")!).ToArray(), Is.EqualTo(new[] { 1, 2 }));
      Assert.That(((IEnumerable<int>)Invoke("YieldStop")!).ToArray(), Is.EqualTo(new[] { 1 }));
      fixture.GetField("NestedCalls")!.SetValue(null, 0);
      Invoke("Nested", true);
      Assert.That(fixture.GetField("NestedCalls")!.GetValue(null), Is.EqualTo(1));
      fixture.GetField("Calls")!.SetValue(null, 0);
      Invoke("ReturnVoid");
      Invoke("CallStatement");
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.EqualTo(1));

      void Activate(Candidate candidate) => active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Activate(candidates.Single(candidate => candidate.Original == "return value;"));
      Assert.That(Invoke("ReturnValue", 7), Is.Zero);
      Activate(candidates.Where(candidate => candidate.Original == "throw new InvalidOperationException();").OrderBy(candidate => candidate.Start).First());
      Assert.That(Invoke("ThrowValue"), Is.Zero);
      Activate(candidates.Single(candidate => candidate.Original == "throw;"));
      Assert.That(Invoke("Rethrow"), Is.Zero);
      Activate(breakCandidates.OrderBy(candidate => candidate.Start).First());
      Assert.That(Invoke("BreakLoop"), Is.EqualTo(3));
      Activate(candidates.Single(candidate => candidate.Original == "continue;"));
      Assert.That(Invoke("ContinueLoop"), Is.EqualTo(3));
      Activate(candidates.Single(candidate => candidate.Original == "goto Target;"));
      Assert.That(Invoke("GotoTarget"), Is.EqualTo(1));
      Activate(candidates.Where(candidate => candidate.Original == "yield return 1;").OrderBy(candidate => candidate.Start).First());
      Assert.That(((IEnumerable<int>)Invoke("YieldValue")!).ToArray(), Is.Empty);
      Activate(candidates.Single(candidate => candidate.Original == "yield break;"));
      Assert.That(((IEnumerable<int>)Invoke("YieldStop")!).ToArray(), Is.EqualTo(new[] { 1, 2 }));
      fixture.GetField("Calls")!.SetValue(null, 0);
      Activate(candidates.Single(candidate => candidate.Original == "return;"));
      Assert.DoesNotThrow(() => Invoke("ReturnVoid"));
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.EqualTo(1));
      fixture.GetField("Calls")!.SetValue(null, 0);
      Activate(candidates.Single(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("return value;", StringComparison.Ordinal)));
      Assert.That(Invoke("ReturnValue", 7), Is.Zero);
      fixture.GetField("Calls")!.SetValue(null, 0);
      Activate(candidates.Single(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("MyMethodCall();", StringComparison.Ordinal)));
      Invoke("CallStatement");
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.Zero);
      Activate(candidates.Single(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("yield return 1; yield return 2;", StringComparison.Ordinal)));
      Assert.That(((IEnumerable<int>)Invoke("YieldValue")!).ToArray(), Is.Empty);
      fixture.GetField("NestedCalls")!.SetValue(null, 0);
      Activate(candidates.Single(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original == "{ NestedCalls++; }"));
      Invoke("Nested", true);
      Assert.That(fixture.GetField("NestedCalls")!.GetValue(null), Is.Zero);
      Activate(candidates.Single(candidate => candidate.Original == "MyMethodCall();"));
      fixture.GetField("Calls")!.SetValue(null, 0);
      Invoke("CallStatement");
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.Zero);

      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "Fixture.csproj");
      var testProject = Path.Combine(testsDirectory, "Fixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "Fixture.cs"), File.ReadAllText(source));
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\Fixture.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "RemovalConformanceTests.cs"),
          "using System; using NUnit.Framework; using System.Linq; [TestFixture] public sealed class RemovalConformanceTests { [Test] public void EveryRemovalMutationIsCheckedAgainstOriginalBehavior() { Assert.That(Fixture.ReturnValue(7), Is.EqualTo(7)); Assert.Throws<InvalidOperationException>(() => Fixture.ThrowValue()); Assert.Throws<ArgumentException>(() => Fixture.ThrowException(new ArgumentException())); Assert.Throws<InvalidOperationException>(() => Fixture.Rethrow()); Assert.That(Fixture.BreakLoop(), Is.EqualTo(1)); Assert.That(Fixture.ContinueLoop(), Is.EqualTo(2)); Assert.That(Fixture.GotoTarget(), Is.EqualTo(2)); Fixture.Calls = 0; Fixture.SwitchBlock(0); Assert.That(Fixture.Calls, Is.EqualTo(1)); Fixture.Calls = 0; Fixture.ReturnVoid(); Assert.That(Fixture.Calls, Is.Zero); Fixture.Calls = 0; Fixture.CallStatement(); Assert.That(Fixture.Calls, Is.EqualTo(1)); Fixture.NestedCalls = 0; Fixture.Nested(true); Assert.That(Fixture.NestedCalls, Is.EqualTo(1)); Assert.That(Fixture.YieldValue(), Is.EqualTo(new[] { 1, 2 })); Assert.That(Fixture.YieldStop(), Is.EqualTo(new[] { 1 })); } }");
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["statement-removal"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(candidates.Length));
      var originalTree = CSharpSyntaxTree.ParseText(File.ReadAllText(source));
      var unreachableCandidates = candidates.Where(candidate =>
      {
        var methodName = originalTree.GetRoot().FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(candidate.Start, candidate.Length))
            .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText;
        return candidate.Original == "return 1;" || candidate.Original == "yield return 2;" && methodName == "YieldStop" ||
            methodName == "InfiniteLoopBlock";
      }).ToArray();
      Assert.That(engineResult.Counters.UncoveredMutants, Is.EqualTo(unreachableCandidates.Length));
      Assert.That(engineResult.Mutants.Where(mutant => mutant.Status == MutationStatus.NoCoverage).Select(mutant => mutant.Original)
          .Order(StringComparer.Ordinal), Is.EqualTo(unreachableCandidates.Select(candidate => candidate.Original).Order(StringComparer.Ordinal)));
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status is MutationStatus.Killed or MutationStatus.Survived or MutationStatus.NoCoverage), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status is not (MutationStatus.Killed or MutationStatus.Survived or MutationStatus.NoCoverage))
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
      Assert.That(engineResult.Mutants.Any(mutant => mutant.Status == MutationStatus.Killed), Is.True);
      Assert.That(engineResult.Mutants.Where(mutant => mutant.Status == MutationStatus.Survived)
          .Select(mutant => $"{mutant.Original} -> {mutant.Replacement}"), Is.EquivalentTo(new[]
          {
            "{ return; Calls++; } -> ;"
          }));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "Fixture.cs" && mutant.Line == 1 && mutant.Column > 0), Is.True);
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
  public void RegexMutationsAreSemanticallySelectedAndActivateInCompiledSource()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenRegexParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "RegexFixture.cs");
      var target = Path.Combine(root, "instrumented", "RegexFixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "using System.Text.RegularExpressions; public static class RegexFixture { private const string ConstantPattern = @\"^abc$\"; public static Regex Anchors() => new Regex(@\"^abc$\"); public static Regex Constant() => new Regex(ConstantPattern); public static Regex Class() => new Regex(\"[abc]\"); public static Regex NegatedClass() => new Regex(\"[^abc]\"); public static Regex Digit() => new Regex(@\"\\d\"); public static Regex Unicode() => new Regex(@\"\\p{Alpha}\"); public static Regex LookAhead() => new Regex(\"(?=abc)\"); public static Regex LookBehind() => new Regex(\"(?<!abc)\"); public static Regex ShortQuantifier() => new Regex(\"abc?\"); public static Regex RangeQuantifier() => new Regex(\"abc{5,8}\"); public static Regex CharacterRange() => new Regex(\"[b-y]\"); public static Regex CapturingGroup() => new Regex(\"(abc)\"); public static Regex NonRegex() => new Regex(\"not a regex token\"); public static Regex Unsupported(string pattern) => new Regex(pattern); public static bool IsMatch(string input) => Anchors().IsMatch(input); }");
      var unsupported = new List<MutationResult>();
      var candidates = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported);
      var regexCandidates = candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Regex)).ToArray();
      Assert.That(regexCandidates, Is.Not.Empty);
      Assert.That(regexCandidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(regexCandidates.Length));
      Assert.That(regexCandidates.Any(candidate => candidate.ReplacementToken.StartsWith("regex:anchor-removal:", StringComparison.Ordinal)), Is.True);
      Assert.That(regexCandidates.Any(candidate => candidate.ReplacementToken.StartsWith("regex:character-class-child-removal:", StringComparison.Ordinal)), Is.True);
      Assert.That(regexCandidates.Any(candidate => candidate.ReplacementToken.StartsWith("regex:quantifier-boundary:", StringComparison.Ordinal)), Is.True);
      Assert.That(regexCandidates.Any(candidate => candidate.ReplacementToken.StartsWith("regex:lookaround-negation:", StringComparison.Ordinal)), Is.True);
      Assert.That(regexCandidates.Any(candidate => candidate.ReplacementToken.StartsWith("regex:group-to-non-capturing:", StringComparison.Ordinal)), Is.True);
      Assert.That(unsupported.Any(mutation => mutation.Original.Contains("new Regex(pattern)", StringComparison.Ordinal) && mutation.Diagnostic?.Contains("compile-time constant", StringComparison.Ordinal) == true), Is.True);
      Assert.That(regexCandidates.All(candidate => candidate.Line > 0 && candidate.File.EndsWith("RegexFixture.cs", StringComparison.Ordinal)), Is.True);
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("RegexFixtureAssembly", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("RegexFixture")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      Assert.That(fixture.GetMethod("IsMatch")!.Invoke(null, ["abc"]), Is.EqualTo(true));
      Assert.That(fixture.GetMethod("IsMatch")!.Invoke(null, ["xabc"]), Is.EqualTo(false));
      var removeStartAnchor = regexCandidates.Single(candidate => candidate.Original.Contains("^abc$", StringComparison.Ordinal) &&
          candidate.Replacement.Contains("new Regex(\"abc$\")", StringComparison.Ordinal));
      active.SetValue(null, int.Parse(removeStartAnchor.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("IsMatch")!.Invoke(null, ["xabc"]), Is.EqualTo(true));
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
  public void EveryDocumentedRegexTransformationGeneratesCompilesAndActivates()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenRegexMatrix", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var parityPath = Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json");
      using var parity = JsonDocument.Parse(File.ReadAllText(parityPath));
      var transformations = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
          .Where(row => row.GetProperty("category").GetString()!.StartsWith("Regex /", StringComparison.Ordinal))
          .Select(row => (From: row.GetProperty("from").GetString()!, To: row.GetProperty("to").GetString()!, Status: row.GetProperty("status").GetString()!)).ToArray();
      var patterns = transformations.Select(transform => transform.From).Distinct(StringComparer.Ordinal).ToArray();
      var project = Path.Combine(root, "RegexMatrix.csproj");
      var source = Path.Combine(root, "RegexMatrix.cs");
      var target = Path.Combine(root, "instrumented", "RegexMatrix.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      var sourceLines = new[] { "using System.Text.RegularExpressions;", "public static class RegexMatrix", "{" }
          .Concat(patterns.Select((pattern, index) => $"  public static Regex Pattern{index}() => new Regex({SyntaxFactory.Literal(pattern).Text});"))
          .Concat(["}"]);
      File.WriteAllLines(source, sourceLines);
      var unsupported = new List<MutationResult>();
      var candidates = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported);
      var regexCandidates = candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Regex)).ToArray();
      Assert.That(regexCandidates, Is.Not.Empty);
      Assert.That(regexCandidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(regexCandidates.Length));
      string ExtractPattern(string expression)
      {
        var creation = CSharpSyntaxTree.ParseText(expression).GetRoot().DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>().Single();
        return ((LiteralExpressionSyntax)creation.ArgumentList!.Arguments[0].Expression).Token.ValueText;
      }

      foreach (var transform in transformations)
      {
        var patternIndex = Array.IndexOf(patterns, transform.From);
        var lineNumber = patternIndex + 4;
        var generated = regexCandidates.Where(candidate => candidate.Line == lineNumber).Select(candidate => ExtractPattern(candidate.Replacement));
        if (transform.Status == "supported")
        {
          Assert.That(generated, Does.Contain(transform.To), $"Missing regex transformation: {transform.From} -> {transform.To}");
        }
        else
        {
          Assert.That(unsupported.Any(mutation => mutation.Line == lineNumber &&
              mutation.Diagnostic?.Contains("invalid in this .NET regex runtime", StringComparison.Ordinal) == true), Is.True,
              $"Unsupported regex transformation lacks a diagnostic: {transform.From} -> {transform.To}");
        }
      }

      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("RegexMatrixAssembly", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("RegexMatrix")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      int PatternIndex(Candidate candidate) => candidate.Line - 4;
      foreach (var candidate in regexCandidates)
      {
        var index = PatternIndex(candidate);
        var method = fixture.GetMethod($"Pattern{index}")!;
        active.SetValue(null, -1);
        Assert.That(((System.Text.RegularExpressions.Regex)method.Invoke(null, null)!).ToString(), Is.EqualTo(patterns[index]));
        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        Assert.That(((System.Text.RegularExpressions.Regex)method.Invoke(null, null)!).ToString(), Is.EqualTo(ExtractPattern(candidate.Replacement)),
            $"Mutation {candidate.Original} -> {candidate.Replacement} did not activate.");
      }
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
  public async Task EverySupportedRegexTransformationExecutesAndKillsInEngineAsync()
  {
    var parityPath = Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json");
    using var parity = JsonDocument.Parse(File.ReadAllText(parityPath));
    var transformations = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
        .Where(row => row.GetProperty("category").GetString()!.StartsWith("Regex /", StringComparison.Ordinal))
        .Select(row => (From: row.GetProperty("from").GetString()!, Status: row.GetProperty("status").GetString()!)).ToArray();
    var supportedRows = transformations.Where(transform => transform.Status == "supported").ToArray();
    var patterns = transformations.Select(transform => transform.From).Distinct(StringComparer.Ordinal).ToArray();
    var root = Path.Combine(Path.GetTempPath(), "MutagenRegexEngineParity", Guid.NewGuid().ToString("N"));
    var productionDirectory = Path.Combine(root, "Production");
    var testsDirectory = Path.Combine(root, "Tests");
    Directory.CreateDirectory(productionDirectory);
    Directory.CreateDirectory(testsDirectory);
    try
    {
      File.WriteAllText(Path.Combine(root, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "RegexMatrix.csproj");
      var testProject = Path.Combine(testsDirectory, "RegexMatrix.Tests.csproj");
      var productionSource = new[] { "using System.Text.RegularExpressions;", "public static class RegexMatrix", "{" }
          .Concat(patterns.Select((pattern, index) => $"  public static Regex Pattern{index}() => new Regex({SyntaxFactory.Literal(pattern).Text});"))
          .Concat(["}"]);
      File.WriteAllLines(Path.Combine(productionDirectory, "RegexMatrix.cs"), productionSource);
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\RegexMatrix.csproj\" /></ItemGroup></Project>");
      var assertions = patterns.Select((pattern, index) => (pattern, index))
          .Where(pair => supportedRows.Any(transform => transform.From == pair.pattern))
          .Select(pair => $"Assert.That(RegexMatrix.Pattern{pair.index}().ToString(), Is.EqualTo({SyntaxFactory.Literal(pair.pattern).Text}));");
      File.WriteAllText(Path.Combine(testsDirectory, "RegexConformanceTests.cs"),
          "using NUnit.Framework; [TestFixture] public sealed class RegexConformanceTests { [Test] public void EveryPatternMatchesItsOriginalValue() { " +
          string.Join(" ", assertions) + " } }");
      var result = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["regex-patterns"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(root, "mutation-results")
      });
      Assert.That(result.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, result.Diagnostics));
      Assert.That(result.Mutants.Count, Is.GreaterThan(supportedRows.Length), "The Regex mutator may generate multiple supported runtime candidates for one documented transformation row.");
      Assert.That(result.UnsupportedMutations, Has.Count.EqualTo(transformations.Length - supportedRows.Length));
      Assert.That(result.Counters.CoveredMutants, Is.EqualTo(result.Mutants.Count));
      Assert.That(result.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, result.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
      Assert.That(result.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "RegexMatrix.cs" && mutant.Line >= 4 && mutant.Column > 0), Is.True);
      Assert.That(result.UnsupportedMutations.All(mutation => mutation.Diagnostic?.Contains("invalid in this .NET regex runtime", StringComparison.Ordinal) == true), Is.True);
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
  public void StringLinqAndMathMutationsCompileAndActivate()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenMethodParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      var target = Path.Combine(root, "instrumented", "Fixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "using System; using System.Linq; public static class Fixture { public static string Literal() => \"foo\"; public static string Interpolated(int x) => $\"foo {x}\"; public static string Empty() => string.Empty; public static bool NullCheck(string x) => string.IsNullOrEmpty(x); public static string Case(string x) => x.ToLower(); public static string Substring(string x) => x.Substring(1); public static char Element(string x) => x.ElementAt(0); public static int Count(int[] x) => x.Count(); public static int Last(int[] x) => x.Last(); public static double Trig(double x) => Math.Sin(x); }");
      var candidates = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None);
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.String)), Is.True);
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.StringMethods)), Is.True);
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.Linq)), Is.True);
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.Math)), Is.True);
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("MethodFixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      Assert.That(compilation.Emit(bytes).Success, Is.True);
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("Fixture")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      Assert.That(fixture.GetMethod("Literal")!.Invoke(null, null), Is.EqualTo("foo"));
      Assert.That(fixture.GetMethod("Case")!.Invoke(null, ["Abc"]), Is.EqualTo("abc"));
      Assert.That(fixture.GetMethod("NullCheck")!.Invoke(null, [null]), Is.EqualTo(true));
      Assert.That(fixture.GetMethod("Count")!.Invoke(null, [new[] { 2, 3 }]), Is.EqualTo(2));
      Assert.That(fixture.GetMethod("Trig")!.Invoke(null, [0.5]), Is.EqualTo(Math.Sin(0.5)));
      void Activate(Candidate candidate) => active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Activate(candidates.Single(candidate => candidate.Original == "\"foo\""));
      Assert.That(fixture.GetMethod("Literal")!.Invoke(null, null), Is.EqualTo(string.Empty));
      Activate(candidates.Single(candidate => candidate.Replacement == "$\"\""));
      Assert.That(fixture.GetMethod("Interpolated")!.Invoke(null, [4]), Is.EqualTo(string.Empty));
      Activate(candidates.Single(candidate => candidate.Replacement == "\"Stryker was here!\""));
      Assert.That(fixture.GetMethod("Empty")!.Invoke(null, null), Is.EqualTo("Stryker was here!"));
      Activate(candidates.Single(candidate => candidate.Replacement == "(x != null)"));
      Assert.That(fixture.GetMethod("NullCheck")!.Invoke(null, [null]), Is.EqualTo(false));
      Activate(candidates.Single(candidate => candidate.Replacement == "x.ToUpper()"));
      Assert.That(fixture.GetMethod("Case")!.Invoke(null, ["abc"]), Is.EqualTo("ABC"));
      Activate(candidates.Single(candidate => candidate.Original == "x.Substring(1)" && candidate.Replacement == "\"\""));
      Assert.That(fixture.GetMethod("Substring")!.Invoke(null, ["abcd"]), Is.EqualTo(string.Empty));
      Activate(candidates.Single(candidate => candidate.Replacement == "'\\0'"));
      Assert.That(fixture.GetMethod("Element")!.Invoke(null, ["abc"]), Is.EqualTo('\0'));
      Activate(candidates.Single(candidate => candidate.Replacement == "x.Sum()"));
      Assert.That(fixture.GetMethod("Count")!.Invoke(null, [new[] { 2, 3 }]), Is.EqualTo(5));
      Activate(candidates.Single(candidate => candidate.Replacement == "Math.Sinh(x)"));
      Assert.That(fixture.GetMethod("Trig")!.Invoke(null, [0.5]), Is.EqualTo(Math.Sinh(0.5)));
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
  public async Task EveryDocumentedStringValueTransformationExecutesAndKillsInEngineAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenStringValueEngineParity", Guid.NewGuid().ToString("N"));
    var productionDirectory = Path.Combine(root, "Production");
    var testsDirectory = Path.Combine(root, "Tests");
    using var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json")));
    var documentedChanges = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
        .Where(row => row.GetProperty("category").GetString() == "Mutation / String Literals (_string_)" &&
            row.GetProperty("status").GetString() == "supported")
        .Select(row => (
            Original: row.GetProperty("from").GetString()!.Replace("x", "value", StringComparison.Ordinal),
            Replacement: row.GetProperty("to").GetString()!.Replace("x", "value", StringComparison.Ordinal)))
        .ToArray();
    Assert.That(documentedChanges, Has.Length.EqualTo(10));
    Directory.CreateDirectory(productionDirectory);
    Directory.CreateDirectory(testsDirectory);
    try
    {
      File.WriteAllText(Path.Combine(root, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "StringFixture.csproj");
      var testProject = Path.Combine(testsDirectory, "StringFixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "StringFixture.cs"), "public static class StringFixture { public static string Literal() => \"foo\"; public static string EmptyLiteral() => \"\"; public static string VerbatimLiteral() => @\"foo\"; public static string Interpolated(int bar) => $\"foo {bar}\"; public static string EmptyField() => string.Empty; public static bool NullOrEmpty(string? value) => string.IsNullOrEmpty(value); public static bool NullOrWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value); }");
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\StringFixture.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "StringValueConformanceTests.cs"),
          "using NUnit.Framework; [TestFixture] public sealed class StringValueConformanceTests { [Test] public void AllStringMutationsMatchOriginalOutputs() { Assert.That(StringFixture.Literal(), Is.EqualTo(\"foo\")); Assert.That(StringFixture.EmptyLiteral(), Is.EqualTo(\"\")); Assert.That(StringFixture.VerbatimLiteral(), Is.EqualTo(\"foo\")); Assert.That(StringFixture.Interpolated(4), Is.EqualTo(\"foo 4\")); Assert.That(StringFixture.EmptyField(), Is.EqualTo(\"\")); Assert.That(StringFixture.NullOrEmpty(null), Is.True); Assert.That(StringFixture.NullOrEmpty(\"\"), Is.True); Assert.That(StringFixture.NullOrEmpty(\"x\"), Is.False); Assert.That(StringFixture.NullOrWhiteSpace(null), Is.True); Assert.That(StringFixture.NullOrWhiteSpace(\"\"), Is.True); Assert.That(StringFixture.NullOrWhiteSpace(\" \"), Is.True); Assert.That(StringFixture.NullOrWhiteSpace(\"x\"), Is.False); } }");
      var result = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["string-values"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(root, "mutation-results")
      });
      Assert.That(result.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, result.Diagnostics));
      Assert.That(result.Mutants, Has.Count.EqualTo(10));
      static string NormalizeReplacement(string replacement) => replacement
          .Replace("(", string.Empty, StringComparison.Ordinal)
          .Replace(")", string.Empty, StringComparison.Ordinal)
          .Replace(" ", string.Empty, StringComparison.Ordinal);
      var generatedChanges = result.Mutants.Select(mutant =>
          $"{NormalizeReplacement(mutant.Original)}->{NormalizeReplacement(mutant.Replacement)}").ToArray();
      var normalizedDocumentedChanges = documentedChanges.Select(change =>
          $"{NormalizeReplacement(change.Original)}->{NormalizeReplacement(change.Replacement)}").ToArray();
      Assert.That(generatedChanges, Is.EquivalentTo(normalizedDocumentedChanges),
          "The E2E execution did not include every transformation from the pinned Stryker matrix.");
      Assert.That(result.Counters.CoveredMutants, Is.EqualTo(10));
      Assert.That(result.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, result.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
      Assert.That(result.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "StringFixture.cs" && mutant.Line == 1 && mutant.Column > 0), Is.True);
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
  public void MethodMutatorsRejectReplacementsWithoutApplicableOverloads()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenMethodOverloads", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source,
          "using System; using System.Linq; public static class Fixture { public static double LogOne(double value) => Math.Log(value); public static double LogTwo(double value) => Math.Log(value, 2); public static int CountWhere(int[] values) => values.Count(value => value > 0); public static int CountAll(int[] values) => values.Count(); }");
      var unsupported = new List<MutationResult>();
      var candidates = Engine.DiscoverCandidates([source], project,
          new MutationOptions { ProjectPath = project, TestProjects = [] }, [], CancellationToken.None, unsupported);

      Assert.That(candidates.Any(candidate => candidate.Original == "Math.Log(value)" && candidate.Replacement == "Math.Exp(value)"), Is.True);
      Assert.That(candidates.Any(candidate => candidate.Original == "Math.Log(value, 2)" && candidate.Replacement == "Math.Pow(value, 2)"), Is.True);
      Assert.That(candidates.Any(candidate => candidate.Original == "values.Count()" && candidate.Replacement == "values.Sum()"), Is.True);
      Assert.That(candidates.Any(candidate => candidate.Replacement == "Math.Pow(value)"), Is.False);
      Assert.That(candidates.Any(candidate => candidate.Replacement == "Math.Exp(value, 2)"), Is.False);
      Assert.That(candidates.Any(candidate => candidate.Replacement == "values.Sum(value => value > 0)"), Is.False);
      Assert.That(unsupported.Any(mutation => mutation.Replacement == "Math.Pow(value)" && mutation.Diagnostic!.Contains("no applicable overload", StringComparison.Ordinal)), Is.True);
      Assert.That(unsupported.Any(mutation => mutation.Replacement == "Math.Exp(value, 2)" && mutation.Diagnostic!.Contains("no applicable overload", StringComparison.Ordinal)), Is.True);
      Assert.That(unsupported.Any(mutation => mutation.Replacement == "values.Sum(value => value > 0)" && mutation.Diagnostic!.Contains("no applicable overload", StringComparison.Ordinal)), Is.True);
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
  public async Task EveryDocumentedMathTransformationCompilesAndActivatesInItsApplicableOverloadAsync()
  {
    var matrixPath = Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json");
    using var matrix = JsonDocument.Parse(File.ReadAllText(matrixPath));
    var rows = matrix.RootElement.GetProperty("documentedTransformations").EnumerateArray()
        .Where(row => row.GetProperty("category").GetString() == "Mutation / Math Methods (_math_)" &&
            row.GetProperty("status").GetString() == "supported")
        .Select(row => (From: MethodName(row.GetProperty("from").GetString()!), To: MethodName(row.GetProperty("to").GetString()!)))
        .ToArray();
    Assert.That(rows, Has.Length.EqualTo(49));

    var root = Path.Combine(Path.GetTempPath(), "MutagenMathMatrix", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "MathFixture.csproj");
      var source = Path.Combine(root, "MathFixture.cs");
      var target = Path.Combine(root, "instrumented", "MathFixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      var methodNames = rows.Select((row, index) => (row, Name: $"Row{index:D2}"))
          .ToDictionary(pair => pair.Name, pair => pair.row, StringComparer.Ordinal);
      var methods = rows.Select((row, index) =>
      {
        var arguments = row.From is "Pow" or "MaxMagnitude" or "MinMagnitude" || row.From == "Log" && row.To == "Pow"
            ? "value, 2d"
            : "value";
        return $"public static double Row{index:D2}(double value) => Math.{row.From}({arguments});";
      });
      var original = "using System; public static class MathFixture { " + string.Join(" ", methods) + " }";
      File.WriteAllText(source, original);
      var unsupported = new List<MutationResult>();
      var options = new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [],
        Mutators = new HashSet<string>(["math-methods"], StringComparer.Ordinal)
      };
      var candidates = Engine.DiscoverCandidates([source], project, options, [], CancellationToken.None, unsupported);
      var actualRows = candidates.Select(candidate => candidate.ReplacementToken).ToHashSet(StringComparer.Ordinal);
      foreach (var row in rows)
      {
        Assert.That(actualRows, Does.Contain(row.From + "->" + row.To), $"Missing Math transformation {row.From} -> {row.To}");
      }

      Assert.That(unsupported.All(mutation => mutation.Status == MutationStatus.Unsupported &&
          mutation.Diagnostic?.Contains("no applicable overload", StringComparison.Ordinal) == true), Is.True);
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      var expectedMethods = candidates.Select((candidate, index) =>
          $"public static double Original{index}(double value) => {candidate.Original}; public static double Mutant{index}(double value) => {candidate.Replacement};");
      var expected = "using System; public static class MathExpected { " + string.Join(" ", expectedMethods) + " }";
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target),
        CSharpSyntaxTree.ParseText(expected, parse, "Expected.cs"),
        CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs")
      };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("MathMatrixFixture_" + Guid.NewGuid().ToString("N"), trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("MathFixture")!;
      var expectedFixture = assembly.GetType("MathExpected")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      var syntaxRoot = CSharpSyntaxTree.ParseText(original, parse, source).GetRoot();
      var methodBySpan = syntaxRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
          .ToDictionary(invocation => (invocation.SpanStart, invocation.Span.Length),
              invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText);

      var possibleInputs = new[] { -10d, -3.5d, -2d, -1d, -0.5d, 0d, 0.25d, 0.5d, 0.75d, 1d, 1.5d, 2d, 3.5d, 10d };
      var engineAssertions = new List<string>();
      for (var index = 0; index < candidates.Count; index++)
      {
        var candidate = candidates[index];
        Assert.That(methodBySpan.TryGetValue((candidate.Start, candidate.Length), out var methodName), Is.True,
            $"No source mapping for {candidate.Original} at {candidate.File}:{candidate.Line}:{candidate.Column}");
        var expectedOriginalMethod = expectedFixture.GetMethod("Original" + index)!;
        var expectedMutantMethod = expectedFixture.GetMethod("Mutant" + index)!;
        var input = possibleInputs.FirstOrDefault(value =>
            Normalize(expectedOriginalMethod.Invoke(null, [value])) != Normalize(expectedMutantMethod.Invoke(null, [value])));
        Assert.That(possibleInputs.Any(value =>
                Normalize(expectedOriginalMethod.Invoke(null, [value])) != Normalize(expectedMutantMethod.Invoke(null, [value]))), Is.True,
            $"No Math input distinguishes {candidate.ReplacementToken}");
        active.SetValue(null, -1);
        var expectedOriginal = expectedOriginalMethod.Invoke(null, [input]);
        Assert.That(fixture.GetMethod(methodName!)!.Invoke(null, [input]), Is.EqualTo(expectedOriginal),
            $"Original behavior changed for {candidate.Original}");
        var expectedNormalized = Normalize(expectedOriginal);
        engineAssertions.Add($"Assert.That(Normalize(MathFixture.{methodName}({input.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}d)), Is.EqualTo({System.Text.Json.JsonSerializer.Serialize(expectedNormalized)}));");
        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        Assert.That(fixture.GetMethod(methodName!)!.Invoke(null, [input]), Is.EqualTo(expectedMutantMethod.Invoke(null, [input])),
            $"Mutation activation failed for {candidate.ReplacementToken}");
      }

      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "MathFixture.csproj");
      var testProject = Path.Combine(testsDirectory, "MathFixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AssemblyName>MathFixture.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "MathFixture.cs"), original);
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\MathFixture.csproj\" /></ItemGroup></Project>");
      var testSource = "using System; using NUnit.Framework; [TestFixture] public sealed class MathConformanceTests { " +
          "private static string Normalize(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty; " +
          "[Test] public void EveryDocumentedMathMutationIsCheckedAgainstOriginalBehavior() { " + string.Join(" ", engineAssertions) + " } }";
      File.WriteAllText(Path.Combine(testsDirectory, "MathConformanceTests.cs"), testSource);
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["math-methods"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(candidates.Count));
      Assert.That(engineResult.Counters.CoveredMutants, Is.EqualTo(candidates.Count));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "MathFixture.cs" && mutant.Line > 0 && mutant.Column > 0), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => candidates.Any(candidate => candidate.Original == mutant.Original && candidate.Replacement == mutant.Replacement)), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }

    static string MethodName(string tableValue) => tableValue[..tableValue.IndexOf('(')];

    static string Normalize(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
  }

  [Test]
  public async Task EveryDocumentedLinqTransformationCompilesAndActivatesAsync()
  {
    var matrixPath = Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json");
    using var matrix = JsonDocument.Parse(File.ReadAllText(matrixPath));
    var rows = matrix.RootElement.GetProperty("documentedTransformations").EnumerateArray()
        .Where(row => row.GetProperty("category").GetString() == "Mutation / Linq Methods (_linq_)" &&
            row.GetProperty("status").GetString() == "supported")
        .Select(row => (From: MethodName(row.GetProperty("from").GetString()!), To: MethodName(row.GetProperty("to").GetString()!)))
        .ToArray();
    Assert.That(rows, Has.Length.EqualTo(34));

    var root = Path.Combine(Path.GetTempPath(), "MutagenLinqMatrix", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "LinqFixture.csproj");
      var source = Path.Combine(root, "LinqFixture.cs");
      var target = Path.Combine(root, "instrumented", "LinqFixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      var expressions = rows.Select(row => LinqExpression(row.From)).ToArray();
      var original = "using System; using System.Linq; public static class LinqFixture { " +
          string.Join(" ", expressions.Select((expression, index) => $"public static object? Row{index:D2}(int[] values) => {expression};")) + " }";
      File.WriteAllText(source, original);
      var options = new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [],
        Mutators = new HashSet<string>(["linq-methods"], StringComparer.Ordinal)
      };
      var unsupported = new List<MutationResult>();
      var candidates = Engine.DiscoverCandidates([source], project, options, [], CancellationToken.None, unsupported);
      var actualRows = candidates.Select(candidate => candidate.ReplacementToken).ToHashSet(StringComparer.Ordinal);
      var parsedTree = CSharpSyntaxTree.ParseText(original, new CSharpParseOptions(LanguageVersion.Preview), source);
      var platformReferences = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var semanticModel = CSharpCompilation.Create("LinqSpanBinding", [parsedTree], platformReferences).GetSemanticModel(parsedTree);
      var maxInvocation = parsedTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
          .Single(invocation => invocation.Expression.ToString() == "values.Max");
      var maxSymbol = semanticModel.GetSymbolInfo(maxInvocation).Symbol as IMethodSymbol;
      var directMutations = MutatorRegistry.CreateDefault(new HashSet<string>(["linq-methods"], StringComparer.Ordinal))
          .Generate(maxInvocation, semanticModel, (_, _) => (true, null)).ToArray();
      Assert.That(directMutations.Select(mutation => mutation.ReplacementToken), Does.Contain("Max->Min"),
          $"Resolved method: {maxSymbol?.Name} on {maxSymbol?.ContainingType.ToDisplayString()} in {maxSymbol?.ContainingNamespace.ToDisplayString()}");
      foreach (var row in rows)
      {
        Assert.That(actualRows, Does.Contain(row.From + "->" + row.To),
            $"Missing LINQ transformation {row.From} -> {row.To}; generated: {string.Join(", ", actualRows.Order(StringComparer.Ordinal))}; unsupported: {string.Join(" | ", unsupported.Select(mutation => mutation.Original + " -> " + mutation.Replacement + ": " + mutation.Diagnostic))}");
      }

      Assert.That(unsupported, Is.Empty, string.Join(Environment.NewLine, unsupported.Select(mutation => mutation.Diagnostic)));
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      var expectedMethods = candidates.Select((candidate, index) =>
          $"public static object? Original{index}(int[] values) => {candidate.Original}; public static object? Mutant{index}(int[] values) => {candidate.Replacement};");
      var expected = "using System; using System.Linq; public static class LinqExpected { " + string.Join(" ", expectedMethods) + " }";
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target),
        CSharpSyntaxTree.ParseText(expected, parse, "Expected.cs"),
        CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs")
      };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("LinqMatrixFixture_" + Guid.NewGuid().ToString("N"), trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("LinqFixture")!;
      var expectedFixture = assembly.GetType("LinqExpected")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      var engineAssertions = new List<string>();
      var syntaxRoot = CSharpSyntaxTree.ParseText(original, parse, source).GetRoot();
      var methodBySpan = syntaxRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
          .ToDictionary(invocation => (invocation.SpanStart, invocation.Span.Length),
              invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText);
      var possibleInputs = new[] { Array.Empty<int>(), [1], [1, 2], [1, 2, 3], [1, 2, 3, 2], [3, 2, 1] };

      for (var index = 0; index < candidates.Count; index++)
      {
        var candidate = candidates[index];
        Assert.That(methodBySpan.TryGetValue((candidate.Start, candidate.Length), out var methodName), Is.True,
            $"No source mapping for {candidate.Original} at {candidate.File}:{candidate.Line}:{candidate.Column}");
        active.SetValue(null, -1);
        var originalMethod = expectedFixture.GetMethod("Original" + index)!;
        var mutantMethod = expectedFixture.GetMethod("Mutant" + index)!;
        var input = possibleInputs.FirstOrDefault(values =>
        {
          return NormalizeInvocation(originalMethod, [values]) != NormalizeInvocation(mutantMethod, [values]);
        });
        Assert.That(input, Is.Not.Null,
            $"No LINQ input distinguishes {candidate.ReplacementToken}: {candidate.Original} -> {candidate.Replacement}; values: {string.Join(" | ", possibleInputs.Select(values => $"[{string.Join(",", values)}]={NormalizeInvocation(originalMethod, [values])}/{NormalizeInvocation(mutantMethod, [values])}"))}");
        var actualOriginal = fixture.GetMethod(methodName!)!;
        var expectedNormalized = NormalizeInvocation(originalMethod, [input]);
        Assert.That(NormalizeInvocation(actualOriginal, [input]), Is.EqualTo(expectedNormalized),
            $"Original behavior changed for {candidate.Original}");
        var inputLiteral = input!.Length == 0 ? "Array.Empty<int>()" : "new[] { " + string.Join(", ", input) + " }";
        if (expectedNormalized.StartsWith("!exception:", StringComparison.Ordinal))
        {
          engineAssertions.Add($"Assert.Throws<{expectedNormalized[11..]}>(() => LinqFixture.{methodName}({inputLiteral}));");
        }
        else
        {
          engineAssertions.Add($"Assert.That(Normalize(LinqFixture.{methodName}({inputLiteral})), Is.EqualTo({System.Text.Json.JsonSerializer.Serialize(expectedNormalized)}));");
        }

        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var actualMutation = fixture.GetMethod(methodName!)!;
        var expectedMutation = expectedFixture.GetMethod("Mutant" + index)!;
        Assert.That(NormalizeInvocation(actualMutation, [input]), Is.EqualTo(NormalizeInvocation(expectedMutation, [input])),
            $"Mutation activation failed for {candidate.ReplacementToken}");
      }

      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "LinqFixture.csproj");
      var testProject = Path.Combine(testsDirectory, "LinqFixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AssemblyName>LinqFixture.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "LinqFixture.cs"), original);
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\LinqFixture.csproj\" /></ItemGroup></Project>");
      var testSource = "using System; using System.Linq; using NUnit.Framework; [TestFixture] public sealed class LinqConformanceTests { " +
          "private static string Normalize(object? value) => value is System.Collections.IEnumerable enumerable and not string ? string.Join(\",\", enumerable.Cast<object?>().Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture))) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty; " +
          "[Test] public void EveryDocumentedLinqMutationIsCheckedAgainstOriginalBehavior() { " + string.Join(" ", engineAssertions) + " } }";
      File.WriteAllText(Path.Combine(testsDirectory, "LinqConformanceTests.cs"), testSource);
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["linq-methods"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(candidates.Count));
      Assert.That(engineResult.Counters.CoveredMutants, Is.EqualTo(candidates.Count));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "LinqFixture.cs" && mutant.Line > 0 && mutant.Column > 0), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => candidates.Any(candidate => candidate.Original == mutant.Original && candidate.Replacement == mutant.Replacement)), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }

    static string MethodName(string tableValue) => tableValue[..tableValue.IndexOf('(')];

    static string Normalize(object? value) => value is System.Collections.IEnumerable enumerable and not string
        ? string.Join(",", enumerable.Cast<object?>().Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture)))
        : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    static string NormalizeInvocation(System.Reflection.MethodInfo method, object?[] arguments)
    {
      try
      {
        return Normalize(method.Invoke(null, arguments));
      }
      catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
      {
        return "!exception:" + exception.InnerException.GetType().FullName;
      }
    }

    static string LinqExpression(string method) => method switch
    {
      "All" => "values.All(value => value > 0)",
      "Any" => "values.Any(value => value > 0)",
      "AsEnumerable" => "values.AsEnumerable()",
      "Average" => "values.Average()",
      "Concat" => "values.Concat(new[] { 2, 4 })",
      "Count" => "values.Count()",
      "Except" => "values.Except(new[] { 2, 4 })",
      "First" => "values.First()",
      "FirstOrDefault" => "values.FirstOrDefault()",
      "Intersect" => "values.Intersect(new[] { 2, 4 })",
      "IntersectBy" => "values.IntersectBy(new[] { 2, 4 }, value => value)",
      "Last" => "values.Last()",
      "Max" => "values.Max()",
      "MaxBy" => "values.MaxBy(value => value)",
      "Min" => "values.Min()",
      "MinBy" => "values.MinBy(value => value)",
      "Order" => "values.Order()",
      "OrderBy" => "values.OrderBy(value => value)",
      "OrderByDescending" => "values.OrderByDescending(value => value)",
      "OrderDescending" => "values.OrderDescending()",
      "Reverse" => "values.Reverse()",
      "Single" => "values.Single(value => value == 3)",
      "SingleOrDefault" => "values.SingleOrDefault(value => value == 3)",
      "Skip" => "values.Skip(1)",
      "SkipLast" => "values.SkipLast(1)",
      "SkipWhile" => "values.SkipWhile(value => value < 3)",
      "Sum" => "values.Sum()",
      "Take" => "values.Take(1)",
      "TakeLast" => "values.TakeLast(1)",
      "TakeWhile" => "values.TakeWhile(value => value < 3)",
      "ThenBy" => "values.OrderBy(value => value / 2).ThenBy(value => value)",
      "ThenByDescending" => "values.OrderBy(value => value / 2).ThenByDescending(value => value)",
      "Union" => "values.Union(new[] { 2, 4 })",
      "UnionBy" => "values.UnionBy(new[] { 2, 4 }, value => value)",
      _ => throw new ArgumentOutOfRangeException(nameof(method), method, "No LINQ fixture expression is registered.")
    };
  }

  [Test]
  public async Task EveryDocumentedStringMethodTransformationCompilesAndActivatesAsync()
  {
    var matrixPath = Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json");
    using var matrix = JsonDocument.Parse(File.ReadAllText(matrixPath));
    var rows = matrix.RootElement.GetProperty("documentedTransformations").EnumerateArray()
        .Where(row => row.GetProperty("category").GetString() == "Mutation / String Methods (_stringmethod_)" &&
            row.GetProperty("status").GetString() == "supported")
        .Select(row => (From: MethodName(row.GetProperty("from").GetString()!), To: row.GetProperty("to").GetString()!))
        .ToArray();
    Assert.That(rows, Has.Length.EqualTo(16));

    var root = Path.Combine(Path.GetTempPath(), "MutagenStringMethodMatrix", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "StringFixture.csproj");
      var source = Path.Combine(root, "StringFixture.cs");
      var target = Path.Combine(root, "instrumented", "StringFixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      var expressions = rows.Select(row => StringMethodExpression(row.From)).ToArray();
      var original = "using System; using System.Linq; public static class StringFixture { " +
          string.Join(" ", expressions.Select((expression, index) => $"public static object? Row{index:D2}(string value) => {expression};")) + " }";
      File.WriteAllText(source, original);
      var options = new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [],
        Mutators = new HashSet<string>(["string-methods", "string-method-constants"], StringComparer.Ordinal)
      };
      var candidates = Engine.DiscoverCandidates([source], project, options, [], CancellationToken.None);
      var replacementSet = candidates.Select(candidate => candidate.Replacement).ToHashSet(StringComparer.Ordinal);
      foreach (var row in rows)
      {
        if (row.To.StartsWith('"') || row.To.StartsWith('\''))
        {
          Assert.That(replacementSet, Does.Contain(row.To), $"Missing string transformation {row.From} -> {row.To}");
        }
        else
        {
          var targetMethod = MethodName(row.To);
          Assert.That(candidates.Any(candidate => candidate.ReplacementToken == row.From + "->" + targetMethod), Is.True,
              $"Missing string transformation {row.From} -> {targetMethod}");
        }
      }

      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      var expectedMethods = candidates.Select((candidate, index) =>
          $"public static object? Original{index}(string value) => {candidate.Original}; public static object? Mutant{index}(string value) => {candidate.Replacement};");
      var expected = "using System; using System.Linq; public static class StringExpected { " + string.Join(" ", expectedMethods) + " }";
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target),
        CSharpSyntaxTree.ParseText(expected, parse, "Expected.cs"),
        CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs")
      };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("StringMethodMatrix_" + Guid.NewGuid().ToString("N"), trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("StringFixture")!;
      var expectedFixture = assembly.GetType("StringExpected")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      var syntaxRoot = CSharpSyntaxTree.ParseText(original, parse, source).GetRoot();
      var methodBySpan = syntaxRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
          .ToDictionary(invocation => (invocation.SpanStart, invocation.Span.Length),
              invocation => invocation.Ancestors().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText);
      var possibleInputs = new[] { " Abca ", "", "a", "ab", "abba", "  a  ", "ABC", "abc", "xyz", " a ", " a", "a " };
      var engineAssertions = new List<string>();

      for (var index = 0; index < candidates.Count; index++)
      {
        var candidate = candidates[index];
        Assert.That(methodBySpan.TryGetValue((candidate.Start, candidate.Length), out var methodName), Is.True,
            $"No source mapping for {candidate.Original} at {candidate.File}:{candidate.Line}:{candidate.Column}");
        var originalMethod = expectedFixture.GetMethod("Original" + index)!;
        var mutantMethod = expectedFixture.GetMethod("Mutant" + index)!;
        var input = possibleInputs.FirstOrDefault(value =>
            NormalizeInvocation(originalMethod, [value]) != NormalizeInvocation(mutantMethod, [value]));
        Assert.That(input, Is.Not.Null, $"No string input distinguishes {candidate.ReplacementToken}: {candidate.Original} -> {candidate.Replacement}");
        active.SetValue(null, -1);
        var actualOriginal = fixture.GetMethod(methodName!)!;
        var expectedOriginal = NormalizeInvocation(originalMethod, [input]);
        Assert.That(NormalizeInvocation(actualOriginal, [input]), Is.EqualTo(expectedOriginal),
            $"Original behavior changed for {candidate.Original}");
        var inputLiteral = System.Text.Json.JsonSerializer.Serialize(input);
        if (expectedOriginal.StartsWith("!exception:", StringComparison.Ordinal))
        {
          engineAssertions.Add($"Assert.Throws<{expectedOriginal[11..]}>(() => StringFixture.{methodName}({inputLiteral}));");
        }
        else
        {
          engineAssertions.Add($"Assert.That(Normalize(StringFixture.{methodName}({inputLiteral})), Is.EqualTo({System.Text.Json.JsonSerializer.Serialize(expectedOriginal)}));");
        }

        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var actualMutation = fixture.GetMethod(methodName!)!;
        Assert.That(NormalizeInvocation(actualMutation, [input]), Is.EqualTo(NormalizeInvocation(mutantMethod, [input])),
            $"Mutation activation failed for {candidate.ReplacementToken}");
      }

      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "StringFixture.csproj");
      var testProject = Path.Combine(testsDirectory, "StringFixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AssemblyName>StringFixture.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "StringFixture.cs"), original);
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\StringFixture.csproj\" /></ItemGroup></Project>");
      var testSource = "using System; using System.Linq; using NUnit.Framework; [TestFixture] public sealed class StringConformanceTests { " +
          "private static string Normalize(object? value) => value is System.Collections.IEnumerable enumerable and not string ? string.Join(\",\", enumerable.Cast<object?>().Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture))) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty; " +
          "[Test] public void EveryDocumentedStringMutationIsCheckedAgainstOriginalBehavior() { " + string.Join(" ", engineAssertions) + " } }";
      File.WriteAllText(Path.Combine(testsDirectory, "StringConformanceTests.cs"), testSource);
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["string-methods", "string-method-constants"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(candidates.Count));
      Assert.That(engineResult.Counters.CoveredMutants, Is.EqualTo(candidates.Count));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "StringFixture.cs" && mutant.Line > 0 && mutant.Column > 0), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => candidates.Any(candidate => candidate.Original == mutant.Original && candidate.Replacement == mutant.Replacement)), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }

    static string MethodName(string tableValue) => tableValue[..tableValue.IndexOf('(')];

    static string Normalize(object? value) => value is System.Collections.IEnumerable enumerable and not string
        ? string.Join(",", enumerable.Cast<object?>().Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture)))
        : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    static string NormalizeInvocation(System.Reflection.MethodInfo method, object?[] arguments)
    {
      try
      {
        return Normalize(method.Invoke(null, arguments));
      }
      catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
      {
        return "!exception:" + exception.InnerException.GetType().FullName;
      }
    }

    static string StringMethodExpression(string method) => method switch
    {
      "ElementAt" => "value.ElementAt(1)",
      "ElementAtOrDefault" => "value.ElementAtOrDefault(1)",
      "EndsWith" => "value.EndsWith(\"a\", StringComparison.Ordinal)",
      "IndexOf" => "value.IndexOf('b')",
      "LastIndexOf" => "value.LastIndexOf('b')",
      "PadLeft" => "value.PadLeft(8, 'x')",
      "PadRight" => "value.PadRight(8, 'x')",
      "StartsWith" => "value.StartsWith(\" \", StringComparison.Ordinal)",
      "Substring" => "value.Substring(1)",
      "ToLower" => "value.ToLower()",
      "ToLowerInvariant" => "value.ToLowerInvariant()",
      "ToUpper" => "value.ToUpper()",
      "ToUpperInvariant" => "value.ToUpperInvariant()",
      "Trim" => "value.Trim()",
      "TrimEnd" => "value.TrimEnd()",
      "TrimStart" => "value.TrimStart()",
      _ => throw new ArgumentOutOfRangeException(nameof(method), method, "No string-method fixture expression is registered.")
    };
  }

  [Test]
  public void EveryDocumentedStringLiteralTransformationCompilesAndActivates()
  {
    var matrixPath = Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json");
    using var matrix = JsonDocument.Parse(File.ReadAllText(matrixPath));
    var rows = matrix.RootElement.GetProperty("documentedTransformations").EnumerateArray()
        .Where(row => row.GetProperty("category").GetString() == "Mutation / String Literals (_string_)" &&
            row.GetProperty("status").GetString() == "supported")
        .Select(row => (From: row.GetProperty("from").GetString()!, To: row.GetProperty("to").GetString()!))
        .ToArray();
    Assert.That(rows, Has.Length.EqualTo(10));

    var root = Path.Combine(Path.GetTempPath(), "MutagenStringLiteralMatrix", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "StringFixture.csproj");
      var source = Path.Combine(root, "StringFixture.cs");
      var target = Path.Combine(root, "instrumented", "StringFixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      var expressions = rows.Select(row => StringLiteralExpression(row.From)).ToArray();
      var original = "public static class StringLiteralFixture { " +
          string.Join(" ", expressions.Select((expression, index) => $"public static object? Row{index:D2}(string x, string bar) => {expression};")) + " }";
      File.WriteAllText(source, original);
      var options = new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [],
        Mutators = new HashSet<string>(["string-values"], StringComparer.Ordinal)
      };
      var unsupported = new List<MutationResult>();
      var candidates = Engine.DiscoverCandidates([source], project, options, [], CancellationToken.None, unsupported);
      var actualReplacements = candidates.Select(candidate => candidate.Replacement).ToHashSet(StringComparer.Ordinal);
      foreach (var row in rows)
      {
        Assert.That(actualReplacements, Does.Contain(row.To), $"Missing string literal transformation {row.From} -> {row.To}");
      }

      Assert.That(unsupported, Is.Empty, string.Join(Environment.NewLine, unsupported.Select(mutation => mutation.Diagnostic)));
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      var expectedMethods = candidates.Select((candidate, index) =>
          $"public static object? Original{index}(string x, string bar) => {candidate.Original}; public static object? Mutant{index}(string x, string bar) => {candidate.Replacement};");
      var expected = "public static class StringLiteralExpected { " + string.Join(" ", expectedMethods) + " }";
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target),
        CSharpSyntaxTree.ParseText(expected, parse, "Expected.cs"),
        CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs")
      };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("StringLiteralMatrix_" + Guid.NewGuid().ToString("N"), trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("StringLiteralFixture")!;
      var expectedFixture = assembly.GetType("StringLiteralExpected")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      var syntaxRoot = CSharpSyntaxTree.ParseText(original, parse, source).GetRoot();
      var methodBySpan = syntaxRoot.DescendantNodes().OfType<ExpressionSyntax>()
          .ToDictionary(expression => (expression.SpanStart, expression.Span.Length),
              expression => expression.Ancestors().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText);

      for (var index = 0; index < candidates.Count; index++)
      {
        var candidate = candidates[index];
        Assert.That(methodBySpan.TryGetValue((candidate.Start, candidate.Length), out var methodName), Is.True,
            $"No source mapping for {candidate.Original} at {candidate.File}:{candidate.Line}:{candidate.Column}");
        active.SetValue(null, -1);
        var actualOriginal = fixture.GetMethod(methodName!)!.Invoke(null, ["source", "value"]);
        var expectedOriginal = expectedFixture.GetMethod("Original" + index)!.Invoke(null, ["source", "value"]);
        Assert.That(Normalize(actualOriginal), Is.EqualTo(Normalize(expectedOriginal)),
            $"Original behavior changed for {candidate.Original}");

        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var actualMutation = fixture.GetMethod(methodName!)!.Invoke(null, ["source", "value"]);
        var expectedMutation = expectedFixture.GetMethod("Mutant" + index)!.Invoke(null, ["source", "value"]);
        Assert.That(Normalize(actualMutation), Is.EqualTo(Normalize(expectedMutation)),
            $"Mutation activation failed for {candidate.ReplacementToken}");
      }
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }

    static string Normalize(object? value) => value is System.Collections.IEnumerable enumerable and not string
        ? string.Join(",", enumerable.Cast<object?>().Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture)))
        : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    static string StringLiteralExpression(string row) => row switch
    {
      "\"foo\"" => "\"foo\"",
      "\"\"" => "\"\"",
      "$\"foo {bar}\"" => "$\"foo {bar}\"",
      "@\"foo\"" => "@\"foo\"",
      "string.Empty" => "string.Empty",
      "string.IsNullOrEmpty(x)" => "string.IsNullOrEmpty(x)",
      "string.IsNullOrWhiteSpace(x)" => "string.IsNullOrWhiteSpace(x)",
      _ => throw new ArgumentOutOfRangeException(nameof(row), row, "No string-literal fixture expression is registered.")
    };
  }

  [Test]
  public async Task EveryDocumentedBinaryAndPatternTransformationCompilesAndActivatesAsync()
  {
    var matrixPath = Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json");
    using var matrix = JsonDocument.Parse(File.ReadAllText(matrixPath));
    var rows = matrix.RootElement.GetProperty("documentedTransformations").EnumerateArray()
        .Where(row => row.GetProperty("status").GetString() == "supported" &&
            row.GetProperty("category").GetString() is "Mutation / Arithmetic Operators (_arithmetic_)" or
                "Mutation / Equality Operators (_equality_)" or "Mutation / Logical Operators (_logical_)" or
                "Mutation / Bitwise Operators (_bitwise_)")
        .Select(row => (Category: row.GetProperty("category").GetString()!, From: row.GetProperty("from").GetString()!,
            To: row.GetProperty("to").GetString()!))
        .ToArray();
    Assert.That(rows, Has.Length.EqualTo(27));

    var root = Path.Combine(Path.GetTempPath(), "MutagenBinaryMatrix", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "BinaryFixture.csproj");
      var source = Path.Combine(root, "BinaryFixture.cs");
      var target = Path.Combine(root, "instrumented", "BinaryFixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      var expressions = rows.Select(BinaryExpression).ToArray();
      var original = "public static class BinaryFixture { " +
          string.Join(" ", expressions.Select((expression, index) => $"public static object? Row{index:D2}(int a, int b, bool flag) => {expression};")) + " }";
      File.WriteAllText(source, original);
      var options = new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [],
        Mutators = new HashSet<string>(["binary-operators", "binary-patterns", "is-patterns"], StringComparer.Ordinal)
      };
      var unsupported = new List<MutationResult>();
      var candidates = Engine.DiscoverCandidates([source], project, options, [], CancellationToken.None, unsupported);
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var syntaxRoot = CSharpSyntaxTree.ParseText(original, parse, source).GetRoot();
      var methodBySpan = syntaxRoot.DescendantNodes().OfType<ExpressionSyntax>()
          .ToDictionary(expression => (expression.SpanStart, expression.Span.Length),
              expression => expression.Ancestors().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText);
      foreach (var row in rows)
      {
        var expectedToken = row.From switch
        {
          "and" => "pattern:or",
          "or" => "pattern:and",
          "is" or "is not" => "pattern:toggle-not",
          "a^b" => "~^",
          _ => row.To
        };
        var rowIndex = Array.IndexOf(rows, row);
        Assert.That(candidates.Any(candidate => candidate.ReplacementToken == expectedToken &&
            methodBySpan.TryGetValue((candidate.Start, candidate.Length), out var methodName) && methodName == $"Row{rowIndex:D2}"), Is.True,
            $"Missing binary transformation {row.From} -> {row.To}");
      }

      Assert.That(unsupported, Is.Empty, string.Join(Environment.NewLine, unsupported.Select(mutation => mutation.Diagnostic)));
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      var expectedMethods = candidates.Select((candidate, index) =>
      {
        var methodName = methodBySpan[(candidate.Start, candidate.Length)];
        var rowIndex = int.Parse(methodName.AsSpan("Row".Length), System.Globalization.CultureInfo.InvariantCulture);
        var originalExpression = expressions[rowIndex];
        var mutantExpression = originalExpression.Replace(candidate.Original, candidate.Replacement, StringComparison.Ordinal);
        Assert.That(mutantExpression, Is.Not.EqualTo(originalExpression), $"Could not apply {candidate.ReplacementToken} to {originalExpression}");
        return $"public static object? Original{index}(int a, int b, bool flag) => {originalExpression}; public static object? Mutant{index}(int a, int b, bool flag) => {mutantExpression};";
      });
      var expected = "public static class BinaryExpected { " + string.Join(" ", expectedMethods) + " }";
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target),
        CSharpSyntaxTree.ParseText(expected, parse, "Expected.cs"),
        CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs")
      };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("BinaryMatrix_" + Guid.NewGuid().ToString("N"), trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("BinaryFixture")!;
      var expectedFixture = assembly.GetType("BinaryExpected")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      var engineAssertions = new List<string>();
      for (var index = 0; index < candidates.Count; index++)
      {
        var candidate = candidates[index];
        Assert.That(methodBySpan.TryGetValue((candidate.Start, candidate.Length), out var methodName), Is.True,
            $"No source mapping for {candidate.Original} at {candidate.File}:{candidate.Line}:{candidate.Column}");
        active.SetValue(null, -1);
        var actualOriginal = fixture.GetMethod(methodName!)!.Invoke(null, [7, 2, true]);
        var expectedOriginal = expectedFixture.GetMethod("Original" + index)!.Invoke(null, [7, 2, true]);
        Assert.That(Normalize(actualOriginal), Is.EqualTo(Normalize(expectedOriginal)),
            $"Original behavior changed for {candidate.Original}");

        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var actualMethod = fixture.GetMethod(methodName!)!;
        var expectedMethod = expectedFixture.GetMethod("Mutant" + index)!;
        var distinguishingArguments = (from a in Enumerable.Range(-20, 41)
                                       from b in Enumerable.Range(-20, 41)
                                       from flag in new[] { false, true }
                                       let args = new object?[] { a, b, flag }
                                       where Normalize(expectedFixture.GetMethod("Original" + index)!.Invoke(null, args)) != Normalize(expectedMethod.Invoke(null, args))
                                       select args).FirstOrDefault();
        Assert.That(distinguishingArguments, Is.Not.Null,
            $"No input distinguishes {candidate.Original} -> {candidate.ReplacementToken}");
        var originalOutcome = expectedFixture.GetMethod("Original" + index)!.Invoke(null, distinguishingArguments);
        var argumentText = string.Join(", ", distinguishingArguments!.Select(FormatLiteral));
        engineAssertions.Add($"Assert.That(BinaryFixture.{methodName}({argumentText}), Is.EqualTo({FormatLiteral(originalOutcome)}));");
        var actualMutation = actualMethod.Invoke(null, distinguishingArguments);
        var expectedMutation = expectedMethod.Invoke(null, distinguishingArguments);
        Assert.That(Normalize(actualMutation), Is.EqualTo(Normalize(expectedMutation)),
            $"Mutation activation failed for {candidate.Original} -> {candidate.ReplacementToken}");
      }

      var endToEndRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(endToEndRoot, "Production");
      var testsDirectory = Path.Combine(endToEndRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(endToEndRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "BinaryFixture.csproj");
      var testProject = Path.Combine(testsDirectory, "BinaryFixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AssemblyName>BinaryFixture.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "BinaryFixture.cs"), original);
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\BinaryFixture.csproj\" /></ItemGroup></Project>");
      var assertionMethod = "using NUnit.Framework; [TestFixture] public sealed class BinaryConformanceTests { [Test] public void EveryDocumentedBinaryMutationIsCheckedAgainstOriginalBehavior() { " +
          string.Join(" ", engineAssertions) + " } }";
      File.WriteAllText(Path.Combine(testsDirectory, "BinaryConformanceTests.cs"), assertionMethod);

      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["binary-operators", "binary-patterns", "is-patterns"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(endToEndRoot, "mutation-results")
      });

      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(candidates.Count));
      Assert.That(engineResult.Counters.CoveredMutants, Is.EqualTo(candidates.Count));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "BinaryFixture.cs" && mutant.Line > 0 && mutant.Column > 0), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => candidates.Any(candidate => candidate.Original == mutant.Original && candidate.Replacement == mutant.Replacement)), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }

    static string Normalize(object? value) => value is System.Collections.IEnumerable enumerable and not string
        ? string.Join(",", enumerable.Cast<object?>().Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture)))
        : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    static string FormatLiteral(object? value) => value switch
    {
      bool boolean => boolean ? "true" : "false",
      int integer => integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
      _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The binary fixture only emits integer and boolean values.")
    };

    static string BinaryExpression((string Category, string From, string To) row)
    {
      if (row.From == "and")
      {
        return "a is > 0 and < 10";
      }
      if (row.From == "or")
      {
        return "a is > 0 or < 10";
      }
      if (row.From == "is")
      {
        return "a is > 0";
      }
      if (row.From == "is not")
      {
        return "a is not > 0";
      }
      if (row.Category == "Mutation / Logical Operators (_logical_)")
      {
        return row.From switch
        {
          "&&" => "flag && a > 0",
          "||" => "flag || a > 0",
          "^" => "flag ^ (a > 0)",
          _ => throw new ArgumentOutOfRangeException(nameof(row), row, "No logical fixture expression is registered.")
        };
      }

      var expression = row.From switch
      {
        "+" => "a + b",
        "-" => "a - b",
        "*" => "a * b",
        "/" => "a / b",
        "%" => "a % b",
        ">" => "a > b",
        ">=" => "a >= b",
        "<" => "a < b",
        "<=" => "a <= b",
        "==" => "a == b",
        "!=" => "a != b",
        "<<" => "a << b",
        ">>" => "a >> b",
        "&" => "a & b",
        "|" => "a | b",
        "a^b" => "a ^ b",
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "No binary fixture expression is registered.")
      };
      return expression;
    }
  }

  [Test]
  public void BinaryOperatorsGenerateCompileAndActivateDocumentedTransformations()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenBinaryParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Operations.csproj");
      var source = Path.Combine(root, "Operations.cs");
      var target = Path.Combine(root, "instrumented", "Operations.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, """
                public static class Operations
                {
                    public static int Divide(int a, int b) => a / b;
                    public static int Remainder(int a, int b) => a % b;
                    public static bool LessEqual(int a, int b) => a <= b;
                    public static int And(int a, int b) => a & b;
                    public static int LeftShift(int a, int b) => a << b;
                    public static int XorInt(int a, int b) => a ^ b;
                    public static bool XorBool(bool a, bool b) => a ^ b;
                    public static int AddAssign(int a, int b) { a += b; return a; }
                }
                """);
      var candidates = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None);
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[]
      {
                CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target),
                CSharpSyntaxTree.ParseText(runtime, parse, "MutationRuntime.cs")
            };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("BinaryMutationFixture", trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var assemblyBytes = new MemoryStream();
      var emit = compilation.Emit(assemblyBytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      assemblyBytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(assemblyBytes);
      var operations = assembly.GetType("Operations")!;
      var runtimeType = assembly.GetType("MutationRuntime")!;
      var activeField = runtimeType.GetField("Active")!;
      object Invoke(string name, params object[] args) => operations.GetMethod(name)!.Invoke(null, args)!;
      Assert.That(Invoke("Divide", 12, 3), Is.EqualTo(4));
      Assert.That(Invoke("Remainder", 5, 2), Is.EqualTo(1));
      Assert.That(Invoke("LessEqual", 2, 3), Is.EqualTo(true));
      Assert.That(Invoke("And", 6, 3), Is.EqualTo(2));
      Assert.That(Invoke("LeftShift", 2, 1), Is.EqualTo(4));
      Assert.That(Invoke("XorInt", 5, 3), Is.EqualTo(6));
      Assert.That(Invoke("XorBool", true, false), Is.EqualTo(true));
      Assert.That(Invoke("AddAssign", 3, 4), Is.EqualTo(7));

      ActivateAndAssert(candidates.Single(candidate => candidate.Original == "a / b"), "Divide", 12, 3, 36);
      ActivateAndAssert(candidates.Single(candidate => candidate.Original == "a % b"), "Remainder", 5, 2, 10);
      ActivateAndAssert(candidates.Single(candidate => candidate.Original == "a <= b" && candidate.Replacement.Contains(" > ", StringComparison.Ordinal)), "LessEqual", 2, 3, false);
      ActivateAndAssert(candidates.Single(candidate => candidate.Original == "a & b"), "And", 6, 3, 7);
      ActivateAndAssert(candidates.Single(candidate => candidate.Original == "a << b" && candidate.Replacement.Contains(" >> ", StringComparison.Ordinal)), "LeftShift", 2, 1, 1);
      ActivateAndAssert(candidates.Single(candidate => candidate.Original == "a ^ b" && candidate.Operator == "Bitwise"), "XorInt", 5, 3, -7);
      ActivateAndAssert(candidates.Single(candidate => candidate.Original == "a ^ b" && candidate.Operator == "Logical"), "XorBool", true, false, false);
      ActivateAndAssert(candidates.Single(candidate => candidate.Original == "a += b"), "AddAssign", 3, 4, -1);

      void ActivateAndAssert(Candidate candidate, string method, object left, object right, object expected)
      {
        activeField.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        Assert.That(Invoke(method, left, right), Is.EqualTo(expected), candidate.Replacement);
        activeField.SetValue(null, -1);
      }
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
  public async Task UnaryConditionalAndCoalescingMutationsCompileAndActivateAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenExpressionParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      var target = Path.Combine(root, "instrumented", "Fixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "public static class Fixture { public static int Negate(int x) => -x; public static int Positive(int x) => +x; public static int Complement(int x) => ~x; public static int PostIncrement(int x) { x++; return x; } public static int PostDecrement(int x) { x--; return x; } public static int PreIncrement(int x) { ++x; return x; } public static int PreDecrement(int x) { --x; return x; } public static int Choose(bool b, int x, int y) => b ? x : y; public static int? Coalesce(int? x, int? y) => x ?? y; public static int Checked(int x) => checked(x + 1); public static int[] Collection() => [1, 2]; public static int[] EmptyCollection() => []; public static bool Negated(bool condition) => !condition; public static bool Branch(bool branchCondition) { if (branchCondition) return true; return false; } public static int WhileBranch(bool whileCondition) { while (whileCondition) return 1; return 0; } public static bool Pattern(int x) => x is > 0 and < 10; }");
      var unsupported = new List<MutationResult>();
      var candidates = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported);
      Assert.That(candidates.Count(candidate => candidate.Operator == nameof(MutationCategory.Unary)), Is.EqualTo(3));
      Assert.That(candidates.Count(candidate => candidate.Operator == nameof(MutationCategory.Update)), Is.EqualTo(4),
          string.Join(Environment.NewLine, candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Update)).Select(candidate => $"{candidate.Original} -> {candidate.Replacement}")));
      Assert.That(candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Conditional))
          .Select(candidate => NormalizeMutation(candidate.Replacement)).ToHashSet(StringComparer.Ordinal), Is.EquivalentTo(new[] { "true?x:y", "false?x:y" }));
      Assert.That(candidates.Count(candidate => candidate.Operator == nameof(MutationCategory.NullCoalescing)), Is.EqualTo(3));
      Assert.That(candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.NullCoalescing))
          .Select(candidate => NormalizeMutation(candidate.Replacement)).ToHashSet(StringComparer.Ordinal), Is.EquivalentTo(new[] { "y??x", "x", "y" }));
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.Checked)), Is.True,
          string.Join(Environment.NewLine, unsupported.Select(mutation => mutation.Diagnostic)));
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.CollectionExpression)), Is.True);
      Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "boolean:negate-condition"), Is.True);
      Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "pattern:or"), Is.True);
      Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "pattern:toggle-not"), Is.True);
      using (var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json"))))
      {
        var rows = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
            .Where(row => row.GetProperty("status").GetString() == "supported")
            .Select(row => (Category: row.GetProperty("category").GetString()!, From: row.GetProperty("from").GetString()!,
                To: row.GetProperty("to").GetString()!)).ToArray();
        string[] Replacements(string category) => rows.Where(row => row.Category == category).Select(row => row.To).ToArray();
        var unaryReplacements = Replacements("Mutation / Unary Operators (_unary_)")
            .Select(value => value.Replace("variable", "x", StringComparison.Ordinal)).ToArray();
        Assert.That(candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Unary))
            .Select(candidate => NormalizeMutation(candidate.Replacement)), Is.SupersetOf(unaryReplacements.Select(NormalizeMutation)));
        var updateReplacements = Replacements("Mutation / Update Operators (_update_)")
            .Select(value => value.Replace("variable", "x", StringComparison.Ordinal)).ToArray();
        Assert.That(candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Update))
            .Select(candidate => NormalizeMutation(candidate.Replacement)), Is.SupersetOf(updateReplacements.Select(NormalizeMutation)));
        var conditionalReplacements = Replacements("Mutation / Conditional Operators (_conditional_)")
            .Select(value => System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(
                    System.Text.RegularExpressions.Regex.Replace(value, @"\bx\b", "b", System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                    @"\ba\b", "x", System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                @"\bb\b", "y", System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToArray();
        Assert.That(candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Conditional))
            .Select(candidate => NormalizeMutation(candidate.Replacement)), Is.SupersetOf(conditionalReplacements.Select(NormalizeMutation)));
        var coalescingReplacements = Replacements("Mutation / Null-coalescing Operators (_nullcoalescing_)")
            .Select(value => value.Replace("a", "x", StringComparison.Ordinal).Replace("b", "y", StringComparison.Ordinal)).ToArray();
        Assert.That(candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.NullCoalescing))
            .Select(candidate => NormalizeMutation(candidate.Replacement)), Is.SupersetOf(coalescingReplacements.Select(NormalizeMutation)));
        Assert.That(Replacements("Mutation / Checked Statements (_checked_)").Select(_ => "x+1"),
            Is.SubsetOf(candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Checked))
                .Select(candidate => NormalizeMutation(candidate.Replacement))));
        var collectionReplacements = Replacements("Mutation / Collection Expressions (_collectionexpression_)");
        Assert.That(candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.CollectionExpression))
            .Select(candidate => NormalizeMutation(candidate.Replacement)), Is.SupersetOf(collectionReplacements.Select(NormalizeMutation)));
        var booleanRows = rows.Where(row => row.Category == "Mutation / Boolean Literals (_boolean_)").ToArray();
        foreach (var row in booleanRows.Where(row => row.From is "true" or "false"))
        {
          Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.Boolean) &&
              candidate.Original == row.From && candidate.Replacement == row.To), Is.True,
              $"Missing documented boolean literal transformation: {row.From} -> {row.To}");
        }
        var negationRow = booleanRows.Single(row => row.From.StartsWith("!", StringComparison.Ordinal));
        var negationTarget = negationRow.To.Replace("person.IsAdult()", "condition", StringComparison.Ordinal);
        Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.Boolean) &&
            candidate.Original.StartsWith("!", StringComparison.Ordinal) &&
            NormalizeMutation(candidate.Replacement) == NormalizeMutation(negationTarget)), Is.True,
            $"Missing documented boolean negation transformation: {negationRow.From} -> {negationRow.To}");
        Assert.That(candidates.Count(candidate => candidate.ReplacementToken == "boolean:negate-condition"), Is.GreaterThanOrEqualTo(
            booleanRows.Count(row => row.From.StartsWith("if(", StringComparison.Ordinal) || row.From.StartsWith("while(", StringComparison.Ordinal))));
      }
      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static bool IsActiveAndHit(int siteId, int id) { Hit(siteId); return IsActive(id); } public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("ExpressionFixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      Assert.That(compilation.Emit(bytes).Success, Is.True);
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("Fixture")!;
      var runtimeType = assembly.GetType("MutationRuntime")!;
      var active = runtimeType.GetField("Active")!;
      Assert.That(fixture.GetMethod("Negate")!.Invoke(null, [3]), Is.EqualTo(-3));
      Assert.That(fixture.GetMethod("Choose")!.Invoke(null, [true, 4, 8]), Is.EqualTo(4));
      Assert.That(fixture.GetMethod("Coalesce")!.Invoke(null, [null, 8]), Is.EqualTo(8));
      Assert.That(fixture.GetMethod("Checked")!.Invoke(null, [4]), Is.EqualTo(5));
      Assert.That(fixture.GetMethod("Collection")!.Invoke(null, null), Is.EqualTo(new[] { 1, 2 }));
      Assert.That(fixture.GetMethod("EmptyCollection")!.Invoke(null, null), Is.EqualTo(Array.Empty<int>()));
      Assert.That(fixture.GetMethod("Branch")!.Invoke(null, [true]), Is.EqualTo(true));
      Assert.That(fixture.GetMethod("Negated")!.Invoke(null, [true]), Is.EqualTo(false));
      Assert.That(fixture.GetMethod("WhileBranch")!.Invoke(null, [true]), Is.EqualTo(1));
      Assert.That(fixture.GetMethod("Pattern")!.Invoke(null, [4]), Is.EqualTo(true));
      foreach (var (original, methodName, expected) in new[]
      {
        ("-x", "Negate", 3), ("+x", "Positive", -3), ("~x", "Complement", 3)
      })
      {
        var unary = candidates.Single(candidate => candidate.Operator == nameof(MutationCategory.Unary) && candidate.Original == original);
        active.SetValue(null, int.Parse(unary.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        Assert.That(fixture.GetMethod(methodName)!.Invoke(null, [3]), Is.EqualTo(expected), $"Unary {original} -> {unary.Replacement}");
      }
      active.SetValue(null, -1);
      foreach (var (original, methodName, expected) in new[]
      {
        ("x++", "PostIncrement", 2), ("x--", "PostDecrement", 4), ("++x", "PreIncrement", 2), ("--x", "PreDecrement", 4)
      })
      {
        var update = candidates.Single(candidate => candidate.Operator == nameof(MutationCategory.Update) && candidate.Original == original);
        active.SetValue(null, int.Parse(update.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        Assert.That(fixture.GetMethod(methodName)!.Invoke(null, [3]), Is.EqualTo(expected), $"Update {original} -> {update.Replacement}");
      }
      active.SetValue(null, -1);
      active.SetValue(null, -1);
      foreach (var conditional in candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Conditional)))
      {
        active.SetValue(null, int.Parse(conditional.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var input = conditional.Replacement.StartsWith("true", StringComparison.Ordinal) ? new object?[] { false, 4, 8 } : new object?[] { true, 4, 8 };
        var expected = conditional.Replacement.StartsWith("true", StringComparison.Ordinal) ? 4 : 8;
        Assert.That(fixture.GetMethod("Choose")!.Invoke(null, input), Is.EqualTo(expected), conditional.Replacement);
      }
      active.SetValue(null, -1);
      foreach (var coalescing in candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.NullCoalescing)))
      {
        active.SetValue(null, int.Parse(coalescing.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var input = NormalizeMutation(coalescing.Replacement) switch
        {
          "x" => new object?[] { null, 8 },
          "y" => new object?[] { 3, 8 },
          "y??x" => new object?[] { 3, 8 },
          _ => throw new AssertionException($"Unexpected null-coalescing mutation {coalescing.Replacement}")
        };
        object? expected = NormalizeMutation(coalescing.Replacement) == "x" ? null : 8;
        Assert.That(fixture.GetMethod("Coalesce")!.Invoke(null, input), Is.EqualTo(expected), coalescing.Replacement);
      }
      active.SetValue(null, -1);
      var checkedCandidate = candidates.Single(candidate => candidate.Operator == nameof(MutationCategory.Checked));
      active.SetValue(null, int.Parse(checkedCandidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("Checked")!.Invoke(null, [int.MaxValue]), Is.EqualTo(int.MinValue));
      active.SetValue(null, -1);
      var collectionCandidate = candidates.Single(candidate => candidate.Operator == nameof(MutationCategory.CollectionExpression) && candidate.Original == "[1, 2]");
      active.SetValue(null, int.Parse(collectionCandidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("Collection")!.Invoke(null, null), Is.EqualTo(Array.Empty<int>()));
      active.SetValue(null, -1);
      var emptyCollectionCandidate = candidates.Single(candidate => candidate.Operator == nameof(MutationCategory.CollectionExpression) && candidate.Original == "[]");
      active.SetValue(null, int.Parse(emptyCollectionCandidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("EmptyCollection")!.Invoke(null, null), Is.EqualTo(new[] { 0 }));
      active.SetValue(null, -1);
      var negated = candidates.Single(candidate => candidate.Original == "!condition" && candidate.Operator == nameof(MutationCategory.Boolean));
      active.SetValue(null, int.Parse(negated.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("Negated")!.Invoke(null, [true]), Is.EqualTo(true));
      active.SetValue(null, -1);
      var branchCondition = candidates.Single(candidate => candidate.ReplacementToken == "boolean:negate-condition" && candidate.Original == "branchCondition");
      active.SetValue(null, int.Parse(branchCondition.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("Branch")!.Invoke(null, [true]), Is.EqualTo(false));
      active.SetValue(null, -1);
      var whileCondition = candidates.Single(candidate => candidate.ReplacementToken == "boolean:negate-condition" && candidate.Original == "whileCondition");
      active.SetValue(null, int.Parse(whileCondition.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("WhileBranch")!.Invoke(null, [true]), Is.EqualTo(0));
      active.SetValue(null, -1);
      var binaryPattern = candidates.Single(candidate => candidate.ReplacementToken == "pattern:or");
      active.SetValue(null, int.Parse(binaryPattern.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("Pattern")!.Invoke(null, [20]), Is.EqualTo(true));
      active.SetValue(null, -1);
      var patternNegation = candidates.Single(candidate => candidate.ReplacementToken == "pattern:toggle-not");
      active.SetValue(null, int.Parse(patternNegation.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("Pattern")!.Invoke(null, [4]), Is.EqualTo(false));

      var engineAssertions = new List<string>();
      string Literal(object? value) => value switch
      {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        int integer => integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        int[] array => "new int[] { " + string.Join(", ", array) + " }",
        _ => throw new ArgumentException($"Unsupported expected-value type {value.GetType().FullName}.")
      };
      void AddAssertions(string method, IEnumerable<(string Arguments, object?[] Values)> cases)
      {
        foreach (var (arguments, values) in cases)
        {
          active.SetValue(null, -1);
          var expected = fixture.GetMethod(method)!.Invoke(null, values);
          engineAssertions.Add($"Assert.That(Fixture.{method}({arguments}), Is.EqualTo({Literal(expected)}));");
        }
      }
      var integers = Enumerable.Range(-3, 9).Select(value => (value.ToString(System.Globalization.CultureInfo.InvariantCulture), new object?[] { value })).ToArray();
      foreach (var method in new[] { "Negate", "Positive", "Complement", "PostIncrement", "PostDecrement", "PreIncrement", "PreDecrement", "Checked" })
      {
        AddAssertions(method, integers);
      }
      AddAssertions("Choose", from condition in new[] { false, true }
                              select ((condition ? "true" : "false") + ", 3, 8", new object?[] { condition, 3, 8 }));
      AddAssertions("Coalesce", from left in new int?[] { null, -2, 0, 3 }
                                from right in new int?[] { null, -1, 2, 5 }
                                select ($"{Literal(left)}, {Literal(right)}", new object?[] { left, right }));
      AddAssertions("Collection", [("", [])]);
      AddAssertions("EmptyCollection", [("", [])]);
      AddAssertions("Negated", new[] { false, true }.Select(value => (value ? "true" : "false", new object?[] { value })));
      AddAssertions("Branch", new[] { false, true }.Select(value => (value ? "true" : "false", new object?[] { value })));
      AddAssertions("WhileBranch", new[] { false, true }.Select(value => (value ? "true" : "false", new object?[] { value })));
      AddAssertions("Pattern", Enumerable.Range(-1, 16).Append(20)
        .Select(value => (value.ToString(System.Globalization.CultureInfo.InvariantCulture), new object?[] { value })));
      engineAssertions.Add("Assert.Throws<System.OverflowException>(() => Fixture.Checked(int.MaxValue));");

      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "Fixture.csproj");
      var testProject = Path.Combine(testsDirectory, "Fixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AssemblyName>Expression.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "Fixture.cs"), File.ReadAllText(source));
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\Fixture.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "ExpressionConformanceTests.cs"),
          "using NUnit.Framework; [TestFixture] public sealed class ExpressionConformanceTests { [Test] public void EveryExpressionMutationIsCheckedAgainstOriginalBehavior() { " +
          string.Join(" ", engineAssertions) + " } }");
      var expressionMutators = new HashSet<string>([
        "unary-update", "conditional-expressions", "null-coalescing", "checked-contexts", "collection-expressions",
        "boolean-conditions", "boolean-literals", "binary-patterns", "is-patterns"
      ], StringComparer.Ordinal);
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = expressionMutators,
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      var expectedCandidates = candidates.Where(candidate => expressionMutators.Contains(
          candidate.ReplacementToken.StartsWith("unary:", StringComparison.Ordinal) ? "unary-update"
          : candidate.ReplacementToken.StartsWith("boolean:", StringComparison.Ordinal) ? "boolean-conditions"
          : candidate.Operator switch
          {
            "Unary" or "Update" => "unary-update",
            "Conditional" => "conditional-expressions",
            "NullCoalescing" => "null-coalescing",
            "Checked" => "checked-contexts",
            "CollectionExpression" => "collection-expressions",
            "Boolean" => "boolean-literals",
            "Logical" => candidate.ReplacementToken.StartsWith("pattern:", StringComparison.Ordinal) ? "binary-patterns" : string.Empty,
            "Equality" => candidate.ReplacementToken.StartsWith("pattern:", StringComparison.Ordinal) ? "is-patterns" : string.Empty,
            _ => string.Empty
          })).ToArray();
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(expectedCandidates.Length));
      Assert.That(engineResult.Counters.CoveredMutants, Is.EqualTo(expectedCandidates.Length));
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "Fixture.cs" && mutant.Line == 1 && mutant.Column > 0), Is.True);
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }

    static string NormalizeMutation(string mutation) => mutation.Replace("(", string.Empty, StringComparison.Ordinal)
        .Replace(")", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
  }

  [Test]
  public async Task DeclaredResourceLocksSerializeConcurrentTestsWithoutDeadlock()
  {
    var locks = new ResourceLockManager();
    var active = 0;
    var peak = 0;
    async Task<bool> Run(IReadOnlyList<string> resourceKeys)
    {
      return await locks.ExecuteAsync(resourceKeys, async () =>
      {
        var now = Interlocked.Increment(ref active);
        int observed;
        do
        {
          observed = Volatile.Read(ref peak); if (observed >= now)
          {
            break;
          }
        }
        while (Interlocked.CompareExchange(ref peak, now, observed) != observed);
        await Task.Delay(75);
        Interlocked.Decrement(ref active);
        return true;
      }, CancellationToken.None);
    }

    var both = await Task.WhenAll(Run(["database", "shared-cache"]), Run(["shared-cache", "database"]));

    Assert.That(both, Is.All.True);
    Assert.That(peak, Is.EqualTo(1));
  }

  [Test]
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
          await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None));
      Assert.That(missingSelection!.Message, Does.Contain("Select one with --framework"));

      var evaluated = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, "net9.0");

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
      var evaluation = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);

      Assert.That(evaluation.TargetFramework, Is.EqualTo("net11.0"));
      Assert.That(evaluation.AssemblyName, Is.EqualTo("Company.Custom.Tests"));
      Assert.That(evaluation.TargetPath, Does.EndWith(Path.Combine("net11.0", "Company.Custom.Tests.dll")));
      diagnostics.Clear();
      var cached = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);
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

      var net8 = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, "net8.0");
      var net9 = await Engine.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, "net9.0");

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
      Assert.That(Engine.FindProjectTreeRoot([product, tests]), Is.EqualTo(sharedProjectRoot));

      File.WriteAllText(Path.Combine(root, "NuGet.config"), "<configuration><packageSources><clear /></packageSources></configuration>");
      Assert.That(Engine.FindProjectTreeRoot([product, tests]), Is.EqualTo(root));
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
      var production = await Engine.GetProjectGraph(productionProject, new RunMetrics(), CancellationToken.None,
          "net11.0", cache, diagnostics);
      var tests = await Engine.GetProjectGraph(testsProject, new RunMetrics(), CancellationToken.None,
          "net11.0", cache, diagnostics);
      var graphProjects = production.ProjectPaths.Concat(tests.ProjectPaths)
          .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

      Assert.That(graphProjects, Has.Length.EqualTo(2));
      Assert.That(Engine.FindProjectTreeRoot(graphProjects), Is.EqualTo(projectDirectory));
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
  public async Task DiscoverUsesStructuredCaseIdentityAndSupportsBoundedSpecialOperatorContexts()
  {
    var result = await MutationEngine.DiscoverAsync(Options(1));

    Assert.That(result.Tests, Is.Not.Empty);
    Assert.That(result.Tests.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(result.Tests.Count));
    Assert.That(result.Tests.Where(t => t.Name.Contains("IsAdult_HandlesAgeBoundary", StringComparison.Ordinal)).Count(), Is.EqualTo(3));
    Assert.That(result.Tests.Where(t => t.Name.Contains("AddAsync_RunsWithFixtureAndStaticInitialization", StringComparison.Ordinal)).Count(), Is.EqualTo(2));
    Assert.That(result.Tests.All(t => t.ExecutorUri == "executor://nunit3testexecutor/"), Is.True);
    Assert.That(result.Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(result.Candidates.Count));
    Assert.That(result.Diagnostics, Has.Some.Contains("string concatenation"));
    Assert.That(result.Diagnostics, Has.None.Contains("user-defined operators"));
    Assert.That(result.Diagnostics, Has.None.Contains("dynamic or unresolved operand type"));
    Assert.That(result.Diagnostics, Has.None.Contains("expression-tree"));
    Assert.That(result.Diagnostics, Has.None.Contains("checked arithmetic"));
    Assert.That(result.Candidates.Any(candidate => candidate.Original == "left + right" && candidate.Line == 18), Is.True,
        "A bound user-defined operator can be mutated when the schema validates its replacement.");
    Assert.That(result.Candidates.Any(candidate => candidate.Original == "left + right" && candidate.Line == 19), Is.True,
        "A dynamic binary expression can be mutated when its runtime probe preserves the evaluation site.");
    Assert.That(result.Candidates.Count(candidate => candidate.Line == 21 && candidate.Operator == "Equality"), Is.EqualTo(2),
        "Expression-tree operators use an in-tree activation and coverage call.");
    Assert.That(result.Candidates.Any(candidate => candidate.Line == 15 && candidate.Operator == "Arithmetic"), Is.True,
        "An arithmetic candidate inside checked remains under the original checked context.");
    Assert.That(result.UnsupportedMutations, Is.Not.Empty);
    Assert.That(result.UnsupportedMutations.All(m => m.Status == MutationStatus.Unsupported && !string.IsNullOrWhiteSpace(m.Diagnostic)), Is.True);
  }

  [Test]
  public void InlineSiteProbesPreserveBoundDynamicCheckedAndExpressionTreeSemantics()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenInlineSiteProbe", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      var target = Path.Combine(root, "instrumented", "Fixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, """
          using System;
          using System.Linq.Expressions;
          public readonly record struct Score(int Value)
          {
            public static Score operator +(Score left, Score right) => new(left.Value + right.Value);
            public static Score operator -(Score left, Score right) => new(left.Value - right.Value);
          }
          public static class Fixture
          {
            public static readonly System.Collections.Generic.List<string> OperandOrder = new();
            public static int LeftValue = 5;
            public static int RightValue = 2;
            public static Score ReadLeftScore() { OperandOrder.Add("left"); return new(2); }
            public static Score ReadRightScore() { OperandOrder.Add("right"); return new(5); }
            public static dynamic ReadLeftDynamic() { OperandOrder.Add("left"); return 10; }
            public static dynamic ReadRightDynamic() { OperandOrder.Add("right"); return 3; }
            public static int ReadLeftInt() { OperandOrder.Add("left"); return LeftValue; }
            public static int ReadRightInt() { OperandOrder.Add("right"); return RightValue; }
            public static Score Combine() => ReadLeftScore() + ReadRightScore();
            public static dynamic DynamicCombine() => ReadLeftDynamic() + ReadRightDynamic();
            public static int CheckedAdd() => checked(ReadLeftInt() + ReadRightInt());
            public static int OffsetCalls;
            public static int NextOffset() { OffsetCalls++; return 3; }
            public static Expression<Func<int, int>> Tree() => value => value + NextOffset();
          }
          """);

      var candidates = Engine.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None);
      var userDefined = candidates.Single(candidate => candidate.Original == "ReadLeftScore() + ReadRightScore()" && candidate.Operator == "Arithmetic");
      var dynamic = candidates.Single(candidate => candidate.Original == "ReadLeftDynamic() + ReadRightDynamic()" && candidate.Operator == "Arithmetic");
      var checkedArithmetic = candidates.Single(candidate => candidate.Original == "ReadLeftInt() + ReadRightInt()" && candidate.Operator == "Arithmetic");
      var expressionTree = candidates.Single(candidate => candidate.Original == "value + NextOffset()" && candidate.Operator == "Arithmetic");
      Assert.That(checkedArithmetic.UseInlineSiteProbe, Is.True);
      Assert.That(expressionTree.UseInlineSiteProbe, Is.True);
      Assert.That(userDefined.UseInlineSiteProbe, Is.False);
      Assert.That(dynamic.UseInlineSiteProbe, Is.False);

      File.Copy(source, target);
      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; static readonly System.Collections.Generic.List<int> Sites = new(); public static bool IsActive(int id) => Active == id; public static void Hit(int id) { if (!Sites.Contains(id)) Sites.Add(id); } public static bool IsActiveAndHit(int siteId, int id) { Hit(siteId); return IsActive(id); } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } public static void Clear() => Sites.Clear(); public static bool HasSite(int id) => Sites.Contains(id); }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target),
        CSharpSyntaxTree.ParseText(runtime, parse, "MutationRuntime.cs")
      };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("InlineSiteProbeFixture", trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var assemblyBytes = new MemoryStream();
      var emit = compilation.Emit(assemblyBytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      assemblyBytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(assemblyBytes);
      var fixture = assembly.GetType("Fixture")!;
      var runtimeType = assembly.GetType("MutationRuntime")!;
      var active = runtimeType.GetField("Active")!;
      var clear = runtimeType.GetMethod("Clear")!;
      var hasSite = runtimeType.GetMethod("HasSite")!;
      var operandOrder = (List<string>)fixture.GetField("OperandOrder")!.GetValue(null)!;
      bool Saw(Candidate candidate) => (bool)hasSite.Invoke(null, [int.Parse(candidate.SiteRuntimeId, System.Globalization.CultureInfo.InvariantCulture)])!;
      void Activate(Candidate candidate) => active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      void Deactivate() => active.SetValue(null, -1);

      var originalScore = fixture.GetMethod("Combine")!.Invoke(null, null)!;
      Assert.That(originalScore.GetType().GetProperty("Value")!.GetValue(originalScore), Is.EqualTo(7));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Deactivate();
      Activate(userDefined);
      operandOrder.Clear();
      var mutatedScore = fixture.GetMethod("Combine")!.Invoke(null, null)!;
      Assert.That(mutatedScore.GetType().GetProperty("Value")!.GetValue(mutatedScore), Is.EqualTo(-3));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Assert.That(Saw(userDefined), Is.True);

      Deactivate();
      operandOrder.Clear();
      Assert.That(fixture.GetMethod("DynamicCombine")!.Invoke(null, null), Is.EqualTo(13));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Activate(dynamic);
      operandOrder.Clear();
      Assert.That(fixture.GetMethod("DynamicCombine")!.Invoke(null, null), Is.EqualTo(7));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Assert.That(Saw(dynamic), Is.True);

      Deactivate();
      operandOrder.Clear();
      var leftValue = fixture.GetField("LeftValue")!;
      var rightValue = fixture.GetField("RightValue")!;
      void SetCheckedOperands(int left, int right) { leftValue.SetValue(null, left); rightValue.SetValue(null, right); }
      object? InvokeChecked() => fixture.GetMethod("CheckedAdd")!.Invoke(null, null);
      SetCheckedOperands(5, 2);
      Assert.That(InvokeChecked(), Is.EqualTo(7));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      clear.Invoke(null, null);
      operandOrder.Clear();
      SetCheckedOperands(int.MaxValue, 1);
      var originalOverflow = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
          InvokeChecked());
      Assert.That(originalOverflow!.InnerException, Is.TypeOf<OverflowException>());
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Assert.That(Saw(checkedArithmetic), Is.True, "The original checked site is recorded before evaluation throws.");
      Activate(checkedArithmetic);
      operandOrder.Clear();
      SetCheckedOperands(5, 2);
      Assert.That(InvokeChecked(), Is.EqualTo(3));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      clear.Invoke(null, null);
      operandOrder.Clear();
      SetCheckedOperands(int.MaxValue, -1);
      var mutatedOverflow = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
          InvokeChecked());
      Assert.That(mutatedOverflow!.InnerException, Is.TypeOf<OverflowException>());
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Assert.That(Saw(checkedArithmetic), Is.True, "The active checked site is recorded before the replacement throws.");

      Deactivate();
      clear.Invoke(null, null);
      var offsetCalls = fixture.GetField("OffsetCalls")!;
      offsetCalls.SetValue(null, 0);
      var expression = (LambdaExpression)fixture.GetMethod("Tree")!.Invoke(null, null)!;
      Assert.That(Saw(expressionTree), Is.False, "Creating an expression tree does not count as evaluating its mutation site.");
      Assert.That(expression.Compile().DynamicInvoke(4), Is.EqualTo(7));
      Assert.That(offsetCalls.GetValue(null), Is.EqualTo(1));
      Assert.That(Saw(expressionTree), Is.True);
      Activate(expressionTree);
      clear.Invoke(null, null);
      offsetCalls.SetValue(null, 0);
      var mutatedExpression = (LambdaExpression)fixture.GetMethod("Tree")!.Invoke(null, null)!;
      Assert.That(offsetCalls.GetValue(null), Is.Zero, "Building a mutated expression tree defers operand evaluation.");
      Assert.That(mutatedExpression.Compile().DynamicInvoke(4), Is.EqualTo(1));
      Assert.That(offsetCalls.GetValue(null), Is.EqualTo(1));
      Assert.That(Saw(expressionTree), Is.True);
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
    finally
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
  }

  [Test]
  public void MutationRuntimeSkipsCoverageHitTrackingWhenProbeCollectionIsDisabled()
  {
    var runtime = Engine.RuntimeSource(3);
    Assert.That(runtime, Does.Contain("ProbeEnabled = !string.IsNullOrEmpty(ProbePath)"));
    Assert.That(runtime, Does.Contain("ProbeEnabled ? new int[3] : Array.Empty<int>()"));
    Assert.That(runtime, Does.Contain("if (ProbeEnabled && Volatile.Read(ref Hits[id]) == 0) { Interlocked.CompareExchange(ref Hits[id], 1, 0); }"));
    Assert.That(runtime, Does.Contain("if (ProbeEnabled) { Hit(id); } return value();"));
    Assert.That(runtime, Does.Contain("ActiveSet?.Contains(id) == true"));
    Assert.That(runtime, Does.Contain("if (!ProbeEnabled || PerTestCoverageEnabled) { return; }"));
    Assert.That(runtime, Does.Contain("static readonly bool ProbeEnabled = !string.IsNullOrEmpty(ProbePath) || PerTestCoverageEnabled;"));

    var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
    var tree = CSharpSyntaxTree.ParseText(runtime, parseOptions);
    var platformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    var compilation = CSharpCompilation.Create("ProbeGateFixture_" + Guid.NewGuid().ToString("N"), [tree], platformReferences,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using var assemblyBytes = new MemoryStream();
    var emit = compilation.Emit(assemblyBytes);
    Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

    var originalProbePath = Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE");
    try
    {
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", null);
      var assembly = System.Reflection.Assembly.Load(assemblyBytes.ToArray());
      var runtimeType = assembly.GetType("MutationRuntime", throwOnError: true)!;
      var enabled = (bool)runtimeType.GetField("ProbeEnabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
      Assert.That(enabled, Is.False);
      Assert.DoesNotThrow(() => runtimeType.GetMethod("Hit")!.Invoke(null, [0]));
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", originalProbePath);
    }
  }

  [Test]
  public void MutationRuntimeRecordsConcurrentProbeHitsOnceWithoutLosingSites()
  {
    var runtime = Engine.RuntimeSource(3);
    var tree = CSharpSyntaxTree.ParseText(runtime, new CSharpParseOptions(LanguageVersion.Preview));
    var platformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    var compilation = CSharpCompilation.Create("ConcurrentProbeFixture_" + Guid.NewGuid().ToString("N"), [tree], platformReferences,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using var assemblyBytes = new MemoryStream();
    var emit = compilation.Emit(assemblyBytes);
    Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine,
        emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

    var originalProbePath = Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE");
    var probePath = Path.Combine(Path.GetTempPath(), "MutagenConcurrentProbe_" + Guid.NewGuid().ToString("N") + ".jsonl");
    try
    {
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", probePath);
      var assembly = System.Reflection.Assembly.Load(assemblyBytes.ToArray());
      var runtimeType = assembly.GetType("MutationRuntime", throwOnError: true)!;
      var hit = runtimeType.GetMethod("Hit")!.CreateDelegate<Action<int>>();
      var drain = runtimeType.GetMethod("GetCoverageData")!.CreateDelegate<Func<int[]>>();
      var observed = new System.Collections.Concurrent.ConcurrentBag<int>();
      Parallel.Invoke(
          () => Parallel.For(0, 100_000, index => hit(index % 2)),
          () =>
          {
            for (var index = 0; index < 1_000; index++)
            {
              foreach (var site in drain()) { observed.Add(site); }
            }
          });
      foreach (var site in drain()) { observed.Add(site); }
      Assert.That(observed, Does.Contain(0));
      Assert.That(observed, Does.Contain(1));

      var hits = (int[])runtimeType.GetField("Hits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
          .GetValue(null)!;
      Assert.That(hits, Is.EqualTo(new[] { 0, 0, 0 }));
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", originalProbePath);
    }
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
  public void PerBatchWorkerCountIsCappedWhenEveryBatchContendsForResourceLocks()
  {
    var batches = new[]
    {
      new MutationBatch(0, ["M1"], ["T1", "T2"], new Dictionary<string, string> { ["T1"] = "M1", ["T2"] = "M1" }, 10, true, 30),
      new MutationBatch(1, ["M2"], ["T3", "T4"], new Dictionary<string, string> { ["T3"] = "M2", ["T4"] = "M2" }, 10, true, 30),
      new MutationBatch(2, ["M3"], ["T5", "T6"], new Dictionary<string, string> { ["T5"] = "M3", ["T6"] = "M3" }, 10, true, 30)
    };
    var locks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["T1"] = ["database-a"],
      ["T2"] = ["database-b"],
      ["T3"] = ["database-a"],
      ["T4"] = ["database-b"],
      ["T5"] = ["database-a"],
      ["T6"] = ["database-b"]
    };

    Assert.That(Engine.GetEffectiveWorkerCount(4, batches, TestHostIsolationPolicy.PerBatch, locks), Is.EqualTo(1));
    Assert.That(Engine.GetEffectiveWorkerCount(4, batches, TestHostIsolationPolicy.PerTest, locks), Is.EqualTo(3));
    Assert.That(Engine.GetEffectiveWorkerCount(4, batches.Take(1).ToArray(), TestHostIsolationPolicy.PerBatch, locks), Is.EqualTo(1));
    Assert.That(Engine.GetEffectiveWorkerCount(4, [], TestHostIsolationPolicy.PerBatch, locks), Is.Zero);

    var independentLocks = new Dictionary<string, IReadOnlyList<string>>(locks, StringComparer.Ordinal)
    {
      ["T3"] = ["database-c"],
      ["T4"] = ["database-d"],
      ["T5"] = ["database-e"],
      ["T6"] = ["database-f"]
    };
    Assert.That(Engine.GetEffectiveWorkerCount(4, batches, TestHostIsolationPolicy.PerBatch, independentLocks), Is.EqualTo(3));
  }

  [Test]
  public void ResourceGroupPlannerKeepsOverlappingLocksTogetherAndPartitionsEachMutantOnce()
  {
    var locks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      ["test-a"] = ["database-a"],
      ["test-b"] = ["database-b"],
      ["test-c"] = ["database-a", "queue-c"],
      ["test-d"] = ["queue-c"]
    };
    var groups = ResourceGroupPlanner.CreateGroups(locks.Keys, locks);
    Assert.That(groups["test-a"].Id, Is.EqualTo(groups["test-c"].Id));
    Assert.That(groups["test-c"].Id, Is.EqualTo(groups["test-d"].Id));
    Assert.That(groups["test-a"].Id, Is.Not.EqualTo(groups["test-b"].Id));

    var testToMutant = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
      ["test-a"] = "mutant-cross-group",
      ["test-b"] = "mutant-local",
      ["test-c"] = "mutant-cross-group",
      ["test-d"] = "mutant-cross-group"
    };
    var batch = new MutationBatch(3, ["mutant-cross-group", "mutant-local"], testToMutant.Keys.ToArray(),
        testToMutant, 100, true, 120);
    var partitions = ResourceGroupPlanner.SplitBatch(batch, groups);
    Assert.That(partitions.SelectMany(partition => partition.MutantIds).Order(StringComparer.Ordinal),
        Is.EqualTo(batch.MutantIds.Order(StringComparer.Ordinal)));
    Assert.That(partitions.SelectMany(partition => partition.TestIds).Order(StringComparer.Ordinal),
        Is.EqualTo(batch.TestIds.Order(StringComparer.Ordinal)));
    Assert.That(partitions, Has.Length.EqualTo(2));
    Assert.That(partitions.Single(partition => partition.MutantIds.Contains("mutant-cross-group")).TestIds,
        Is.EquivalentTo(new[] { "test-a", "test-c", "test-d" }));
  }

  [Test]
  public async Task ResourceGroupPolicyKeepsCrossGroupMutantsInOneLockedExecutionUnit()
  {
    const string testFilter = "AddAsync_RunsWithFixtureAndStaticInitialization";
    var discoveryOptions = Options(2, mode: MutationExecutionMode.Batched, isolationPolicy: TestHostIsolationPolicy.PerBatch,
        testFilter: testFilter);
    var discovery = await MutationEngine.DiscoverAsync(discoveryOptions);
    Assert.That(discovery.Tests, Has.Length.EqualTo(2));
    var locks = discovery.Tests.OrderBy(test => test.DisplayName, StringComparer.Ordinal)
        .Select((test, index) => (test.Id, Resource: (IReadOnlyList<string>)[$"independent-db-{index}"]))
        .ToDictionary(pair => pair.Id, pair => pair.Resource, StringComparer.Ordinal);

    var batchedOutput = NewOutputDirectory();
    var groupedOutput = NewOutputDirectory();
    try
    {
      var batched = await MutationEngine.RunAsync(Options(2, batchedOutput, MutationExecutionMode.Batched,
          TestHostIsolationPolicy.PerBatch, testFilter, locks));
      var grouped = await MutationEngine.RunAsync(Options(2, groupedOutput, MutationExecutionMode.Batched,
          TestHostIsolationPolicy.PerResourceGroup, testFilter, locks));

      Assert.That(grouped.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, grouped.Diagnostics));
      Assert.That(grouped.Mutants.Select(mutant => $"{mutant.Id}|{mutant.Status}").Order(StringComparer.Ordinal),
          Is.EqualTo(batched.Mutants.Select(mutant => $"{mutant.Id}|{mutant.Status}").Order(StringComparer.Ordinal)));
      Assert.That(grouped.Counters.MutationTestExecutions, Is.EqualTo(grouped.ExecutionPlan!.Statistics.EstimatedTestExecutions));
      var profilesByName = grouped.Tests.ToDictionary(test => test.DisplayName, test =>
          grouped.ExecutionProfiles.Single(profile => profile.TestId == test.Id).ResourceIsolationGroup, StringComparer.Ordinal);
      Assert.That(profilesByName.Values.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(2));
      var crossGroupMutants = grouped.Mutants.Where(mutant => mutant.CoveringTests
          .Select(testName => profilesByName[testName]).Distinct(StringComparer.Ordinal).Skip(1).Any()).ToArray();
      Assert.That(crossGroupMutants, Is.Not.Empty, "The fixture must exercise a mutant covered by tests in separate resource groups.");
      Assert.That(grouped.Diagnostics, Has.Some.Contains("ResourceGroupScheduling"));
    }
    finally
    {
      if (Directory.Exists(batchedOutput))
      {
        Directory.Delete(batchedOutput, true);
      }
      if (Directory.Exists(groupedOutput))
      {
        Directory.Delete(groupedOutput, true);
      }
    }
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
      var buildOutput = $"{source}(1,44): error CS0029: Cannot implicitly convert type\r\n{source}(1,2): error CS0103: unrelated error";

      var failures = Engine.FindInvalidCandidates(buildOutput, [invalid, unrelated], root);

      Assert.That(failures, Has.Count.EqualTo(1));
      Assert.That(failures[0].Candidate.Id, Is.EqualTo("bad"));
      Assert.That(failures[0].Diagnostic, Does.Contain("CS0029"));
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

      Engine.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      var rewritten = File.ReadAllText(target);
      var returnRoot = candidates.First(candidate => candidate.Original.Contains("&&", StringComparison.Ordinal));
      var nestedComparison = candidates.First(candidate => candidate.Operator == "Equality");

      Assert.That(rewritten, Does.Contain($"MutationRuntime.Hit({returnRoot.SiteRuntimeId})"));
      Assert.That(rewritten, Does.Contain($"MutationRuntime.Probe({nestedComparison.SiteRuntimeId}"));
      Assert.That(rewritten.IndexOf($"MutationRuntime.Hit({returnRoot.SiteRuntimeId})", StringComparison.Ordinal),
          Is.LessThan(rewritten.IndexOf("return", StringComparison.Ordinal)));
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
  public async Task PlanKeepsThrowingExpressionCoveredWithoutExecutingMutants()
  {
    var output = NewOutputDirectory();
    try
    {
      var result = await MutationEngine.PlanAsync(Options(1, output));
      var throwingTest = result.Tests.Single(t => t.DisplayName == "ThrowingOperand_StillEntersCoverageSite");
      var throwingMutation = result.Mutants.First(m => m.Original.Contains("value() > 10", StringComparison.Ordinal));

      Assert.That(result.OverallStatus, Is.EqualTo("Planned"));
      Assert.That(result.ReportSchemaVersion, Is.EqualTo(4));
      Assert.That(result.UnsupportedMutations, Is.Not.Empty);
      Assert.That(result.UnsupportedMutations.All(mutation => mutation.Status == MutationStatus.Unsupported), Is.True);
      Assert.That(result.Counters.MutationTestExecutions, Is.Zero);
      Assert.That(result.Mutants.Where(m => m.Status != MutationStatus.NoCoverage).All(m => m.Status == MutationStatus.Pending), Is.True);
      Assert.That(result.TestToSites[throwingTest.Id], Does.Contain(throwingMutation.SiteId));
      Assert.That(result.CoverageStatuses.Values, Is.All.EqualTo(CoverageStatus.CoverageSuccess));
      Assert.That(File.Exists(result.JsonReportPath), Is.True);
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }

  [Test]
  public void SharedBatchRunSettingsDisableFrameworkParallelism()
  {
    var settings = System.Xml.Linq.XDocument.Parse(TestPlatformSession.RunSettings("probe.jsonl", "1,2",
        serializeTests: true, capturePerTestCoverage: true));
    var settingsWithoutCoverageCollector = System.Xml.Linq.XDocument.Parse(
        TestPlatformSession.RunSettings("probe.jsonl", "1,2", serializeTests: true));
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
    Assert.That(Engine.SupportsSerializedRunSettings("executor://nunit3testexecutor/"), Is.True);
    Assert.That(Engine.SupportsSerializedRunSettings("executor://xunit/VsTestRunner2/netcoreapp"), Is.False);
    Assert.That(Engine.SupportsSerializedRunSettings("executor://mstestadapter/v2"), Is.False);
    Assert.That(Engine.SupportsSerializedRunSettings("executor://unknown"), Is.False);
    Assert.That(Engine.SupportsSerializedRunSettings("executor://nunit3testexecutor-custom/"), Is.False);
    Assert.That(Engine.SupportsSerializedRunSettings("executor://mstestadapter-lookalike/"), Is.False);
  }

  [Test]
  public async Task PerBatchCoverageCollectorPreservesPerTestCoverageWithOneBaselineHost()
  {
    var output = NewOutputDirectory();
    try
    {
      var result = await MutationEngine.RunAsync(Options(2, output, MutationExecutionMode.Batched, TestHostIsolationPolicy.PerBatch));
      Assert.That(result.OverallStatus, Is.EqualTo("Completed"));
      Assert.That(result.Counters.BaselineTestExecutions, Is.EqualTo(result.Tests.Count));
      Assert.That(result.CoverageStatuses, Has.Count.EqualTo(result.Tests.Count));
      Assert.That(result.CoverageStatuses.Values, Is.All.EqualTo(CoverageStatus.CoverageSuccess));
      Assert.That(result.TestToSites.Keys.Order(StringComparer.Ordinal),
          Is.EqualTo(result.Tests.Select(test => test.Id).Order(StringComparer.Ordinal)));
      Assert.That(result.Counters.TestHostProcessStarts, Is.EqualTo(result.Counters.MutationBatches + 2));
      Assert.That(result.Diagnostics, Has.Some.Contains("BaselineCoverageCollector"));
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }

  [Test]
  public async Task IsolatedBatchedAndAutoModesProduceEquivalentResults()
  {
    MutationRunResult? reference = null;
    foreach (var (mode, isolation) in new[]
    {
      (MutationExecutionMode.Isolated, TestHostIsolationPolicy.PerTest),
      (MutationExecutionMode.Batched, TestHostIsolationPolicy.PerTest),
      (MutationExecutionMode.Auto, TestHostIsolationPolicy.PerTest),
      (MutationExecutionMode.Batched, TestHostIsolationPolicy.PerBatch)
    })
    {
      var output = NewOutputDirectory();
      try
      {
        var result = await MutationEngine.RunAsync(Options(2, output, mode, isolation));
        Assert.That(result.OverallStatus, Is.EqualTo("Completed"));
        Assert.That(result.Mutants.Count, Is.EqualTo(34));
        Assert.That(result.KilledCount, Is.EqualTo(25));
        Assert.That(result.SurvivedCount, Is.EqualTo(8));
        Assert.That(result.NoCoverageCount, Is.EqualTo(1));
        Assert.That(result.Counters.BaselineTestExecutions, Is.EqualTo(18));
        Assert.That(result.CoverageStatuses, Has.Count.EqualTo(18));
        Assert.That(result.CoverageStatuses.Values, Is.All.EqualTo(CoverageStatus.CoverageSuccess));
        Assert.That(result.Counters.MutationTestExecutions, Is.GreaterThan(0));
        Assert.That(result.ExecutionPlan, Is.Not.Null);
        Assert.That(result.ExecutionProfiles, Has.Count.EqualTo(result.Tests.Count));
        Assert.That(result.ExecutionProfiles.All(profile => profile.IsolationRequired == (isolation != TestHostIsolationPolicy.PerBatch) && profile.TimeoutSeconds == 120), Is.True);
        Assert.That(result.ExecutionProfiles.All(profile => profile.FixtureAffinity != null), Is.True);
        Assert.That(result.ExecutionPlan!.SelectedMode, Is.EqualTo(mode == MutationExecutionMode.Auto ? MutationExecutionMode.Isolated : mode));
        Assert.That(result.Counters.MutationBatches, Is.EqualTo(result.ExecutionPlan.Batches.Count));
        Assert.That(result.ExecutionPlan.Batches.SelectMany(batch => batch.MutantIds).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(33));
        Assert.That(result.Counters.TestHostProcessStarts, Is.EqualTo(isolation == TestHostIsolationPolicy.PerBatch
            ? result.Counters.MutationBatches + 2
            : result.Counters.TotalTests + result.Counters.MutationTestExecutions + 1));
        if (isolation == TestHostIsolationPolicy.PerBatch)
        {
          Assert.That(result.ExecutionProfiles.All(profile => profile.TestHostIsolationPolicy == TestHostIsolationPolicy.PerBatch &&
              profile.TimeoutPolicy == TestRunTimeoutPolicy.AggregatePerBatch &&
              profile.TestsSerializedInSharedHost &&
              profile.EligibleExecutionModes.SequenceEqual([MutationExecutionMode.Batched])), Is.True);
          Assert.That(result.Counters.TestHostProcessStarts,
              Is.LessThan(result.Counters.TotalTests + result.Counters.MutationTestExecutions + 1));
        }
        Assert.That(result.Mutants.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(result.Mutants.Count));
        Assert.That(File.Exists(result.JsonReportPath), Is.True);
        Assert.That(File.Exists(result.HtmlReportPath), Is.True);

        if (reference is null)
        {
          reference = result;
        }
        else
        {
          Assert.That(result.Mutants.Select(m => $"{m.Id}|{m.Status}").Order(StringComparer.Ordinal),
              Is.EqualTo(reference.Mutants.Select(m => $"{m.Id}|{m.Status}").Order(StringComparer.Ordinal)));
          if (isolation == TestHostIsolationPolicy.PerBatch)
          {
            Assert.That(result.Counters.MutationTestExecutions, Is.EqualTo(result.ExecutionPlan!.Statistics.EstimatedTestExecutions));
          }
          else
          {
            Assert.That(result.Counters.MutationTestExecutions, Is.LessThanOrEqualTo(reference.Counters.MutationTestExecutions));
          }
          Assert.That(result.TestToSites.Keys.Order(StringComparer.Ordinal), Is.EqualTo(reference.TestToSites.Keys.Order(StringComparer.Ordinal)));
          foreach (var testId in reference.TestToSites.Keys)
          {
            Assert.That(result.TestToSites[testId].Order(StringComparer.Ordinal), Is.EqualTo(reference.TestToSites[testId].Order(StringComparer.Ordinal)));
          }
        }
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

  [Test]
  public async Task PerMutantTestHostPolicyReducesHostStartsAndPreservesMutationOutcomes()
  {
    var output = NewOutputDirectory();
    try
    {
      var result = await MutationEngine.RunAsync(Options(2, output, MutationExecutionMode.Isolated, TestHostIsolationPolicy.PerMutant));

      Assert.That(result.OverallStatus, Is.EqualTo("Completed"));
      Assert.That(result.Mutants.Count, Is.EqualTo(34));
      Assert.That(result.KilledCount, Is.EqualTo(25));
      Assert.That(result.SurvivedCount, Is.EqualTo(8));
      Assert.That(result.NoCoverageCount, Is.EqualTo(1));
      Assert.That(result.Mutants.All(mutant => mutant.Status is MutationStatus.Killed or MutationStatus.Survived or MutationStatus.NoCoverage), Is.True);
      Assert.That(result.ExecutionProfiles.All(profile => !profile.IsolationRequired &&
          profile.TestHostIsolationPolicy == TestHostIsolationPolicy.PerMutant), Is.True);
      Assert.That(result.ExecutionProfiles.All(profile => profile.TimeoutPolicy == TestRunTimeoutPolicy.AggregatePerMutant), Is.True);
      Assert.That(result.Diagnostics, Has.Some.Contains("BaselineCoverageCollector"));
      Assert.That(result.Counters.TestHostProcessStarts,
          Is.LessThanOrEqualTo(2 + result.Counters.TotalMutants - result.Counters.UncoveredMutants));
      Assert.That(result.Counters.TestHostProcessStarts,
          Is.LessThan(result.Counters.TotalTests + result.Counters.MutationTestExecutions + 1));
      Assert.That(result.ReportSchemaVersion, Is.EqualTo(4));
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }

  [Test]
  public async Task PerMutantHostPolicySerializesTestsWithDeclaredResourceLocksInOneHost()
  {
    var discoveryOptions = Options(2, NewOutputDirectory(), MutationExecutionMode.Isolated, TestHostIsolationPolicy.PerMutant,
        testFilter: "Add_ReturnsCorrectSum");
    var discovery = await MutationEngine.DiscoverAsync(discoveryOptions);
    var selectedTest = discovery.Tests.Single(test => test.DisplayName.Contains("Add_ReturnsCorrectSum", StringComparison.Ordinal));
    var resourceLocks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
      [selectedTest.Id] = ["shared-database"]
    };
    var output = NewOutputDirectory();
    try
    {
      var result = await MutationEngine.RunAsync(Options(2, output, MutationExecutionMode.Isolated,
          TestHostIsolationPolicy.PerMutant, "Add_ReturnsCorrectSum", resourceLocks));

      var executionProfile = result.ExecutionProfiles.Single(profile => profile.TestId == selectedTest.Id);
      Assert.That(executionProfile.IsolationRequired, Is.False);
      Assert.That(executionProfile.TestHostIsolationPolicy, Is.EqualTo(TestHostIsolationPolicy.PerMutant));
      Assert.That(executionProfile.TimeoutPolicy, Is.EqualTo(TestRunTimeoutPolicy.AggregatePerMutant));
      Assert.That(executionProfile.TestsSerializedInSharedHost, Is.True);
      Assert.That(executionProfile.ResourceLocks, Does.Contain("shared-database"));
      Assert.That(result.Diagnostics, Has.None.Contains("TestHostIsolationFallback"));
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }

  [Test]
  public async Task PerMutantExecutionStopsAfterTheFirstAttributableFailure()
  {
    var project = Path.Combine(Root, "benchmarks", "IsolationSample", "IsolationSample.csproj");
    var tests = Path.Combine(Root, "benchmarks", "IsolationSample.Tests", "IsolationSample.Tests.csproj");
    var output = NewOutputDirectory();
    var marker = Path.Combine(Path.GetTempPath(), "MutagenPerMutantBail", Guid.NewGuid().ToString("N"), "later-test-ran.txt");
    var previousMarker = Environment.GetEnvironmentVariable("MUTAGEN_PER_MUTANT_BAIL_MARKER");
    try
    {
      var discoveryOptions = new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [tests],
        TestFilter = "MutantBailoutTests",
        Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
        TestHostIsolationPolicy = TestHostIsolationPolicy.PerMutant,
        ExecutionMode = MutationExecutionMode.Isolated,
        MaxWorkers = 1,
        TimeoutSeconds = 120
      };
      var discovery = await MutationEngine.DiscoverAsync(discoveryOptions);
      Assert.That(discovery.Tests, Has.Count.EqualTo(2));
      var locks = discovery.Tests.ToDictionary(test => test.Id,
          _ => (IReadOnlyList<string>)["mutant-bailout"], StringComparer.Ordinal);
      Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
      Environment.SetEnvironmentVariable("MUTAGEN_PER_MUTANT_BAIL_MARKER", marker);

      var result = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = project,
        TestProjects = [tests],
        TestFilter = "MutantBailoutTests",
        Mutators = discoveryOptions.Mutators,
        TestHostIsolationPolicy = TestHostIsolationPolicy.PerMutant,
        ExecutionMode = MutationExecutionMode.Isolated,
        ResourceLocksByTestId = locks,
        MaxWorkers = 1,
        TimeoutSeconds = 120,
        OutputDirectory = output
      });

      Assert.That(result.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, result.Diagnostics));
      Assert.That(result.Mutants, Is.Not.Empty);
      Assert.That(result.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True);
      Assert.That(result.Mutants.All(mutant => mutant.KillingTests.Any(name => name.Contains("BoundaryComparisonKillsEveryRelationalMutant", StringComparison.Ordinal))), Is.True);
      Assert.That(result.Counters.MutationTestExecutions, Is.EqualTo(result.Mutants.Count));
      Assert.That(File.Exists(marker), Is.False, "The later covered case must not run after the first test kills each mutant.");
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTAGEN_PER_MUTANT_BAIL_MARKER", previousMarker);
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }

      var markerDirectory = Path.GetDirectoryName(marker)!;
      if (Directory.Exists(markerDirectory))
      {
        Directory.Delete(markerDirectory, true);
      }
    }
  }

  [Test]
  public async Task SqliteIntegrationFixtureUsesDiscoveredShardLocksEndToEnd()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenSqliteLockE2E", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var previousDatabaseRoot = Environment.GetEnvironmentVariable("MUTAGEN_ISOLATION_SQLITE_ROOT");
    Environment.SetEnvironmentVariable("MUTAGEN_ISOLATION_SQLITE_ROOT", Path.Combine(root, "database"));
    var productionProject = Path.Combine(Root, "benchmarks", "IsolationSample", "IsolationSample.csproj");
    var testProject = Path.Combine(Root, "benchmarks", "IsolationSample.Tests", "IsolationSample.Tests.csproj");
    var sharedOptions = new MutationOptions
    {
      ProjectPath = productionProject,
      TestProjects = [testProject],
      Mutators = new HashSet<string>(["binary-operators"], StringComparer.Ordinal),
      MaxWorkers = 4,
      TimeoutSeconds = 120,
      CacheDirectory = Path.Combine(root, "cache")
    };

    try
    {
      var discovery = await MutationEngine.DiscoverAsync(sharedOptions);
      Assert.That(discovery.Tests, Has.Length.EqualTo(2));
      var lockMap = discovery.Tests.ToDictionary(test => test.Id, test =>
      {
        var opening = test.DisplayName.LastIndexOf('(');
        var closing = test.DisplayName.LastIndexOf(')');
        Assert.That(opening, Is.GreaterThan(0), $"Unexpected isolation test display name: {test.DisplayName}");
        Assert.That(closing, Is.GreaterThan(opening), $"Unexpected isolation test display name: {test.DisplayName}");
        var group = int.Parse(test.DisplayName.AsSpan(opening + 1, closing - opening - 1), System.Globalization.CultureInfo.InvariantCulture);
        return (IReadOnlyList<string>)[$"sqlite-shard-{group % 4}"];
      }, StringComparer.Ordinal);
      var independentSelected = discovery.Tests.ToArray();
      Assert.That(independentSelected, Has.Length.EqualTo(2));
      Assert.That(independentSelected, Has.Length.EqualTo(2));
      async Task<MutationRunResult> RunIndependentResourcePolicy(TestHostIsolationPolicy isolationPolicy)
      {
        return await MutationEngine.RunAsync(new MutationOptions
        {
          ProjectPath = productionProject,
          TestProjects = [testProject],
          Mutators = sharedOptions.Mutators,
          MaxWorkers = 4,
          TimeoutSeconds = 120,
          ExecutionMode = MutationExecutionMode.Batched,
          TestHostIsolationPolicy = isolationPolicy,
          TestFilter = string.Join(';', independentSelected.Select(test => test.DisplayName)),
          ResourceLocksByTestId = lockMap,
          CacheDirectory = sharedOptions.CacheDirectory,
          OutputDirectory = Path.Combine(root, $"report-{isolationPolicy}")
        });
      }

      var batchedReference = await RunIndependentResourcePolicy(TestHostIsolationPolicy.PerBatch);
      var resourceGroupResult = await RunIndependentResourcePolicy(TestHostIsolationPolicy.PerResourceGroup);
      Assert.That(batchedReference.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, batchedReference.Diagnostics));
      Assert.That(resourceGroupResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, resourceGroupResult.Diagnostics));
      Assert.That(resourceGroupResult.Mutants.Select(mutation => mutation.Status),
          Is.EquivalentTo([MutationStatus.Killed, MutationStatus.Survived]));
      Assert.That(resourceGroupResult.Mutants.Select(mutant => $"{mutant.Id}|{mutant.Status}").Order(StringComparer.Ordinal),
          Is.EqualTo(batchedReference.Mutants.Select(mutant => $"{mutant.Id}|{mutant.Status}").Order(StringComparer.Ordinal)));
      Assert.That(resourceGroupResult.TestToSites.Keys.Order(StringComparer.Ordinal),
          Is.EqualTo(batchedReference.TestToSites.Keys.Order(StringComparer.Ordinal)));
      foreach (var testId in batchedReference.TestToSites.Keys)
      {
        Assert.That(resourceGroupResult.TestToSites[testId].Order(StringComparer.Ordinal),
            Is.EqualTo(batchedReference.TestToSites[testId].Order(StringComparer.Ordinal)));
      }
      Assert.That(resourceGroupResult.Counters.MutationTestExecutions,
          Is.EqualTo(resourceGroupResult.ExecutionPlan!.Statistics.EstimatedTestExecutions));
      Assert.That(resourceGroupResult.Diagnostics, Has.Some.Contains("ResourceGroupScheduling"));
      Assert.That(resourceGroupResult.ExecutionProfiles.Select(profile => profile.ResourceIsolationGroup)
          .Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(2));
      Assert.That(resourceGroupResult.ExecutionProfiles.All(profile => !profile.IsolationRequired &&
          profile.TestHostIsolationPolicy == TestHostIsolationPolicy.PerResourceGroup &&
          profile.TimeoutPolicy == TestRunTimeoutPolicy.AggregatePerBatch &&
          profile.TestsSerializedInSharedHost &&
          profile.EligibleExecutionModes.SequenceEqual([MutationExecutionMode.Batched])), Is.True);
      Assert.That(resourceGroupResult.Counters.TestHostProcessStarts, Is.GreaterThan(2));
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTAGEN_ISOLATION_SQLITE_ROOT", previousDatabaseRoot);
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
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

  private sealed class CustomIncrementMutator(int expectedRightValue = 1) : IMutator<BinaryExpressionSyntax>
  {
    public MutatorMetadata Metadata { get; } = new($"custom-increment-{expectedRightValue}", 1, MutationCategory.Arithmetic,
        new HashSet<string>(["addition of one"], StringComparer.Ordinal));

    public IEnumerable<MutationDescription> Generate(BinaryExpressionSyntax node, SemanticModel semanticModel,
        Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
    {
      if (!node.IsKind(SyntaxKind.AddExpression) || node.Right is not LiteralExpressionSyntax literal ||
          !Equals(literal.Token.Value, expectedRightValue))
      {
        yield break;
      }

      var validity = validate(node, semanticModel);
      yield return new(Metadata, node, $"({node.Left}) - ({node.Right})", "custom:subtract", node.ToString(),
          validity.Supported ? null : validity.Reason)
      {
        ApplicationKind = MutationApplicationKind.ExpressionReplacement
      };
    }
  }

  private static MutationOptions Options(int workers, string? output = null, MutationExecutionMode mode = MutationExecutionMode.Auto,
      TestHostIsolationPolicy isolationPolicy = TestHostIsolationPolicy.PerTest, string? testFilter = null,
      IReadOnlyDictionary<string, IReadOnlyList<string>>? resourceLocks = null) => new()
      {
        ProjectPath = AppProject,
        TestProjects = [TestProject],
        MaxWorkers = workers,
        OutputDirectory = output ?? NewOutputDirectory(),
        ExecutionMode = mode,
        TestHostIsolationPolicy = isolationPolicy,
        TestFilter = testFilter,
        ResourceLocksByTestId = resourceLocks ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        TimeoutSeconds = 120
      };

  private static string NewOutputDirectory() => Path.Combine(Path.GetTempPath(), "MutationFramework.EngineTests", Guid.NewGuid().ToString("N"));

  private static void CopyProjectSources(string sourceRoot, string destinationRoot)
  {
    Directory.CreateDirectory(destinationRoot);
    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                 .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("obj", StringComparison.OrdinalIgnoreCase))))
    {
      var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
      Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
      File.Copy(file, destination, true);
    }
  }

  private static string FindRepositoryRoot()
  {
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
    {
      if (File.Exists(Path.Combine(current.FullName, "MutationFramework.slnx")))
      {
        return current.FullName;
      }
    }

    throw new DirectoryNotFoundException("Unable to locate MutationFramework.slnx from the test output directory.");
  }
}
