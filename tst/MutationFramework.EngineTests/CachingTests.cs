using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

using static MutationFramework.EngineTests.TestWorkspace;

[TestFixture]
public sealed class CachingTests
{
  [TestCase(false)]
  [TestCase(true)]
  [Category("Component")]
  public async Task SemanticAndCandidateCachesInvalidateWhenOnlyAReferencedTypeChanges(bool externalReference)
  {
    var directory = NewOutputDirectory();
    Directory.CreateDirectory(Path.Combine(directory, "Product"));
    Directory.CreateDirectory(Path.Combine(directory, "Dependency"));
    try
    {
      var project = Path.Combine(directory, "Product", "Product.csproj");
      var dependency = Path.Combine(directory, "Dependency", "Dependency.csproj");
      var dependencySource = Path.Combine(directory, "Dependency", "Dependency.cs");
      File.WriteAllText(dependency, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
      File.WriteAllText(dependencySource, "public static class Dependency { public static bool Value => true; }");
      var referenceXml = externalReference
          ? "<Reference Include=\"Dependency\"><HintPath>../External/Dependency.dll</HintPath></Reference>"
          : "<ProjectReference Include=\"../Dependency/Dependency.csproj\" />";
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup>" + referenceXml + "</ItemGroup></Project>");
      async Task RefreshExternalReference()
      {
        if (!externalReference)
        {
          return;
        }
        var build = await ProcessRunner.RunProcess("dotnet", ["build", dependency, "--nologo", "-v:q"], directory, null, 60, new RunMetrics(), CancellationToken.None);
        Assert.That(build.ExitCode, Is.Zero, build.Output);
        var external = Path.Combine(directory, "External");
        Directory.CreateDirectory(external);
        File.Copy(Path.Combine(directory, "Dependency", "bin", "Debug", "net11.0", "Dependency.dll"), Path.Combine(external, "Dependency.dll"), overwrite: true);
      }
      await RefreshExternalReference();
      File.WriteAllText(Path.Combine(directory, "Product", "Product.cs"), "public static class Product { public static object Compute() => Dependency.Value ^ Dependency.Value; }");
      var cache = new ArtifactCache(Path.Combine(directory, "cache"));
      var diagnostics = new List<string>();
      var options = new MutationOptions { ProjectPath = project, TestProjects = [], Mutators = new HashSet<string>(["binary-operators"]) };
      async Task<List<Candidate>> Discover()
      {
        var evaluation = await CompilationWorkspace.GetProductionGraph(project, [], new RunMetrics(), CancellationToken.None, null, cache, diagnostics);
        return await MutationDiscovery.DiscoverCandidatesCached(evaluation.CompileFiles, project, options, diagnostics, CancellationToken.None, [], evaluation, cache);
      }
      var first = await Discover();
      Assert.That(first.Single().ReplacementToken, Is.EqualTo("=="));
      diagnostics.Clear();
      var cached = await Discover();
      Assert.That(cached.Single().Id, Is.EqualTo(first.Single().Id));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: semantic-compilation", StringComparison.Ordinal)), Is.True);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: mutation-discovery", StringComparison.Ordinal)), Is.True);
      File.WriteAllText(dependencySource, "public static class Dependency { public static int Value => 1; }");
      await RefreshExternalReference();
      diagnostics.Clear();
      var changed = await Discover();
      Assert.That(changed.Single().ReplacementToken, Is.EqualTo("~^"));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheInvalidated: semantic-compilation", StringComparison.Ordinal)), Is.True);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheInvalidated: mutation-discovery", StringComparison.Ordinal)), Is.True);
    }
    finally { Directory.Delete(directory, true); }
  }

  [Test]
  [Category("Component")]
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
  [Category("Component")]
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
  [Category("EndToEnd")]
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
      tamperedResult["Payload"]!["Result"]!["Status"] = (int)MutationStatus.Survived;
      tamperedResult["IntegrityHash"] = CacheIdentity.Hash(tamperedResult["Payload"]!.ToJsonString());
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
  [Category("Component")]
  public async Task MutationDiscoveryCacheInvalidatesWhenRegisteredMutatorsChange()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenMutatorCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Product.csproj");
      var source = Path.Combine(root, "Product.cs");
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
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
      var first = await MutationDiscovery.DiscoverCandidatesCached([source], project, firstOptions, firstDiagnostics, CancellationToken.None, [], evaluation, cache);
      var cachedDiagnostics = new List<string>();
      var cached = await MutationDiscovery.DiscoverCandidatesCached([source], project, firstOptions, cachedDiagnostics, CancellationToken.None, [], evaluation, cache);
      var differentRegistry = new MutatorRegistry([new CustomIncrementMutator(2)]);
      var changedDiagnostics = new List<string>();
      var changed = await MutationDiscovery.DiscoverCandidatesCached([source], project,
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
  [Category("Unit")]
  public void CacheEnvironmentFingerprintIgnoresPerInvocationAgentMetadata()
  {
    var original = Environment.GetEnvironmentVariable("CODEX_TOOL_CALL_ID");
    try
    {
      Environment.SetEnvironmentVariable("CODEX_TOOL_CALL_ID", "invocation-one");
      var first = CacheIdentity.EnvironmentFingerprint();
      Environment.SetEnvironmentVariable("CODEX_TOOL_CALL_ID", "invocation-two");

      Assert.That(CacheIdentity.EnvironmentFingerprint(), Is.EqualTo(first));
    }
    finally
    {
      Environment.SetEnvironmentVariable("CODEX_TOOL_CALL_ID", original);
    }
  }

  [Test]
  [Category("Unit")]
  public void CacheEnvironmentFingerprintIncludesOtherCodexEnvironmentConfiguration()
  {
    const string name = "CODEX_MUTATION_CACHE_TEST_CONFIGURATION";
    var original = Environment.GetEnvironmentVariable(name);
    try
    {
      Environment.SetEnvironmentVariable(name, "configuration-one");
      var first = CacheIdentity.EnvironmentFingerprint();
      Environment.SetEnvironmentVariable(name, "configuration-two");

      Assert.That(CacheIdentity.EnvironmentFingerprint(), Is.Not.EqualTo(first));
    }
    finally
    {
      Environment.SetEnvironmentVariable(name, original);
    }
  }

  [TestCase("TEMP")]
  [TestCase("TMP")]
  [TestCase("TMPDIR")]
  [TestCase("NUMBER_OF_PROCESSORS")]
  [TestCase("SESSIONNAME")]
  [Category("Unit")]
  public void CacheEnvironmentFingerprintIncludesStandardTestEnvironment(string name)
  {
    var original = Environment.GetEnvironmentVariable(name);
    try
    {
      Environment.SetEnvironmentVariable(name, "mutagen-cache-test-value");
      var first = CacheIdentity.EnvironmentFingerprint();
      Environment.SetEnvironmentVariable(name, "mutagen-cache-test-value-changed");

      Assert.That(CacheIdentity.EnvironmentFingerprint(), Is.Not.EqualTo(first), name);
    }
    finally
    {
      Environment.SetEnvironmentVariable(name, original);
    }
  }

  [Test]
  [Category("Component")]
  public void CacheEnvironmentFingerprintIncludesNonDotnetPathChanges()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenPathFingerprint", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var firstDirectory = Path.Combine(root, "first");
    var secondDirectory = Path.Combine(root, "second");
    Directory.CreateDirectory(firstDirectory);
    Directory.CreateDirectory(secondDirectory);
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
      Environment.SetEnvironmentVariable("PATH", firstDirectory);
      var first = CacheIdentity.EnvironmentFingerprint();
      Environment.SetEnvironmentVariable("PATH", secondDirectory);

      Assert.That(CacheIdentity.EnvironmentFingerprint(), Is.Not.EqualTo(first));
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
  [Category("Component")]
  public void CacheEnvironmentFingerprintIgnoresRandomCommandWrapperPathEntries()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenCommandPathFingerprint", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var firstWrapper = Path.Combine(root, "codex-arg0first");
    var secondWrapper = Path.Combine(root, "codex-arg0second");
    Directory.CreateDirectory(firstWrapper);
    Directory.CreateDirectory(secondWrapper);
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
      Environment.SetEnvironmentVariable("PATH", firstWrapper);
      var first = CacheIdentity.EnvironmentFingerprint();
      Environment.SetEnvironmentVariable("PATH", secondWrapper);

      Assert.That(CacheIdentity.EnvironmentFingerprint(), Is.EqualTo(first));
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
  [Category("Unit")]
  public void CacheEnvironmentFingerprintIncludesCurrentCulture()
  {
    var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
    var originalUiCulture = System.Globalization.CultureInfo.CurrentUICulture;
    try
    {
      System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
      System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
      var first = CacheIdentity.EnvironmentFingerprint();
      System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
      System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

      Assert.That(CacheIdentity.EnvironmentFingerprint(), Is.Not.EqualTo(first));
    }
    finally
    {
      System.Threading.Thread.CurrentThread.CurrentCulture = originalCulture;
      System.Threading.Thread.CurrentThread.CurrentUICulture = originalUiCulture;
    }
  }

  [Test]
  [Category("Component")]
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
      var first = CacheIdentity.EnvironmentFingerprint();
      Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, secondRoot, firstRoot));

      Assert.That(CacheIdentity.EnvironmentFingerprint(), Is.Not.EqualTo(first));
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
  [Category("EndToEnd")]
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
  [Category("EndToEnd")]
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
      corruptCoverage["Payload"]!["Sites"] = null;
      corruptCoverage["IntegrityHash"] = CacheIdentity.Hash(corruptCoverage["Payload"]!.ToJsonString());
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
  [Category("EndToEnd")]
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
  [Category("Component")]
  public async Task ProjectEvaluationCacheTracksProjectAndSourceInputs()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenProjectEvaluationCache", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems><TargetFramework>net11.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Fixture.cs\" /></ItemGroup></Project>");
      File.WriteAllText(source, "public class Fixture { public int Value => 1 + 2; }");
      var cache = new ArtifactCache(Path.Combine(root, ".mutagen-cache"));
      var metrics = new RunMetrics();
      var diagnostics = new List<string>();
      var first = await CompilationWorkspace.GetCompileFiles(project, metrics, CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(first.CompileFiles, Does.Contain(source));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheMiss: project-evaluation", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));
      diagnostics.Clear();
      await CompilationWorkspace.GetCompileFiles(project, metrics, CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: project-evaluation", StringComparison.Ordinal)), Is.True);
      File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems><TargetFramework>net11.0</TargetFramework><AssemblyName>Fixture</AssemblyName><DefineConstants>FEATURE_CHANGED</DefineConstants></PropertyGroup><ItemGroup><Compile Include=\"Fixture.cs\" /></ItemGroup></Project>");
      diagnostics.Clear();
      var changed = await CompilationWorkspace.GetCompileFiles(project, metrics, CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(changed.ParseOptions.PreprocessorSymbolNames, Does.Contain("FEATURE_CHANGED"));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheInvalidated: project-evaluation", StringComparison.Ordinal)), Is.True);
      var options = new MutationOptions { ProjectPath = project, TestProjects = [] };
      var unsupported = new List<MutationResult>();
      diagnostics.Clear();
      await MutationDiscovery.DiscoverCandidatesCached(changed.CompileFiles, project, options, diagnostics, CancellationToken.None, unsupported, changed, cache);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheMiss: mutation-discovery", StringComparison.Ordinal)), Is.True);
      diagnostics.Clear();
      await MutationDiscovery.DiscoverCandidatesCached(changed.CompileFiles, project, options, diagnostics, CancellationToken.None, unsupported, changed, cache);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: mutation-discovery", StringComparison.Ordinal)), Is.True);
      File.WriteAllText(source, "public class Fixture { public int Value => 3 + 4; }");
      diagnostics.Clear();
      var changedSources = await CompilationWorkspace.GetCompileFiles(project, metrics, CancellationToken.None, cache: cache, diagnostics: diagnostics);
      diagnostics.Clear();
      await MutationDiscovery.DiscoverCandidatesCached(changedSources.CompileFiles, project, options, diagnostics, CancellationToken.None, unsupported, changedSources, cache);
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
  [Category("Component")]
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

      var first = await CompilationWorkspace.GetProjectGraph(project, new RunMetrics(), CancellationToken.None, "net11.0", cache, diagnostics);
      Assert.That(first.TargetFramework, Is.EqualTo("net11.0"));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheMiss: project-framework-selection", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));

      diagnostics.Clear();
      var repeated = await CompilationWorkspace.GetProjectGraph(project, new RunMetrics(), CancellationToken.None, "net11.0", cache, diagnostics);
      Assert.That(repeated.TargetFramework, Is.EqualTo("net11.0"));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: project-framework-selection", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));

      File.WriteAllText(project,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>");
      diagnostics.Clear();
      var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
          await CompilationWorkspace.GetProjectGraph(project, new RunMetrics(), CancellationToken.None, "net11.0", cache, diagnostics));
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
  [Category("Component")]
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
      var first = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);

      Assert.That(first.CompileFiles, Does.Contain(source));
      Assert.That(first.BuildInputs, Does.Contain(packages));
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheMiss: project-evaluation", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));

      diagnostics.Clear();
      await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(diagnostics.Any(message => message.StartsWith("CacheHit: project-evaluation", StringComparison.Ordinal)), Is.True,
          string.Join(Environment.NewLine, diagnostics));

      File.WriteAllText(packages,
          "<Project><ItemGroup><PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.4\" /></ItemGroup></Project>");
      diagnostics.Clear();
      var changed = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);

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
  [Category("Component")]
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
      var first = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);
      Assert.That(first.AssemblyName, Is.EqualTo("Before"));

      File.WriteAllText(importedProps, "<Project><PropertyGroup><AssemblyName>After</AssemblyName></PropertyGroup></Project>");
      diagnostics.Clear();
      var changed = await CompilationWorkspace.GetCompileFiles(project, new RunMetrics(), CancellationToken.None, cache: cache, diagnostics: diagnostics);

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
}
