using MutationFramework.Sdk;
using NUnit.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

namespace MutationFramework.EngineTests;

using static MutationFramework.EngineTests.TestWorkspace;

[TestFixture]
public sealed class ExpressionConformanceTests
{
  [Test]
  [Category("EndToEnd")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, options, [], CancellationToken.None, unsupported);
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
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
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
      var references = PlatformReferences;
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
  [Category("Component")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None);
      File.Copy(source, target);
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
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
  [Category("EndToEnd")]
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
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported);
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
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static bool IsActiveAndHit(int siteId, int id) { Hit(siteId); return IsActive(id); } public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = PlatformReferences;
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

}
