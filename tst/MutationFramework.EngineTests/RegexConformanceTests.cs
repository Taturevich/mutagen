using MutationFramework.Sdk;
using NUnit.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

namespace MutationFramework.EngineTests;

using static MutationFramework.EngineTests.TestWorkspace;

[TestFixture]
public sealed class RegexConformanceTests
{
  [Test]
  [Category("Component")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported);
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
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = PlatformReferences;
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
  [Category("Component")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported);
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
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = PlatformReferences;
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
  [Category("EndToEnd")]
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

}
