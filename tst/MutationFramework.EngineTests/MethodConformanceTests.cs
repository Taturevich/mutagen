using MutationFramework.Sdk;
using NUnit.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

namespace MutationFramework.EngineTests;

using static MutationFramework.EngineTests.TestWorkspace;

[TestFixture]
public sealed class MethodConformanceTests
{
  [Test]
  [Category("Component")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None);
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.String)), Is.True);
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.StringMethods)), Is.True);
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.Linq)), Is.True);
      Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.Math)), Is.True);
      File.Copy(source, target);
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = PlatformReferences;
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
  [Category("EndToEnd")]
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
  [Category("Component")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project,
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
  [Category("EndToEnd")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, options, [], CancellationToken.None, unsupported);
      var actualRows = candidates.Select(candidate => candidate.ReplacementToken).ToHashSet(StringComparer.Ordinal);
      foreach (var row in rows)
      {
        Assert.That(actualRows, Does.Contain(row.From + "->" + row.To), $"Missing Math transformation {row.From} -> {row.To}");
      }

      Assert.That(unsupported.All(mutation => mutation.Status == MutationStatus.Unsupported &&
          mutation.Diagnostic?.Contains("no applicable overload", StringComparison.Ordinal) == true), Is.True);
      File.Copy(source, target);
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
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
      var references = PlatformReferences;
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
  [Category("EndToEnd")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, options, [], CancellationToken.None, unsupported);
      var actualRows = candidates.Select(candidate => candidate.ReplacementToken).ToHashSet(StringComparer.Ordinal);
      var parsedTree = CSharpSyntaxTree.ParseText(original, new CSharpParseOptions(LanguageVersion.Preview), source);
      var platformReferences = PlatformReferences;
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
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
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
      var references = PlatformReferences;
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
  [Category("EndToEnd")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, options, [], CancellationToken.None);
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
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
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
      var references = PlatformReferences;
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
  [Category("Component")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, options, [], CancellationToken.None, unsupported);
      var actualReplacements = candidates.Select(candidate => candidate.Replacement).ToHashSet(StringComparer.Ordinal);
      foreach (var row in rows)
      {
        Assert.That(actualReplacements, Does.Contain(row.To), $"Missing string literal transformation {row.From} -> {row.To}");
      }

      Assert.That(unsupported, Is.Empty, string.Join(Environment.NewLine, unsupported.Select(mutation => mutation.Diagnostic)));
      File.Copy(source, target);
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
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
      var references = PlatformReferences;
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

}
