using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class ParityMatrixEvidenceTests
{
  private static readonly string[] BinaryConformanceSuites =
  [
    "EveryDocumentedBinaryAndPatternTransformationCompilesAndActivatesAsync"
  ];

  private static readonly string[] ExpressionConformanceSuites =
  [
    "UnaryConditionalAndCoalescingMutationsCompileAndActivateAsync"
  ];

  private static readonly string[] RegexConformanceSuites =
  [
    "EveryDocumentedRegexTransformationGeneratesCompilesAndActivates",
    "EverySupportedRegexTransformationExecutesAndKillsInEngineAsync"
  ];

  private static readonly IReadOnlyDictionary<string, string[]> MutationConformanceSuites =
      new Dictionary<string, string[]>(StringComparer.Ordinal)
      {
        ["Mutation / Arithmetic Operators (_arithmetic_)"] = BinaryConformanceSuites,
        ["Mutation / Equality Operators (_equality_)"] = BinaryConformanceSuites,
        ["Mutation / Logical Operators (_logical_)"] = BinaryConformanceSuites,
        ["Mutation / Bitwise Operators (_bitwise_)"] = BinaryConformanceSuites,
        ["Mutation / Boolean Literals (_boolean_)"] = ExpressionConformanceSuites,
        ["Mutation / Assignment Statements (_assignment_)"] =
        [
          "AssignmentOperatorMutationsHaveCompleteCompileableGenerationAsync"
        ],
        ["Mutation / Initialization (_initializer_)"] =
        [
          "InitializerMutationsCompileActivateAndKillInEngineAsync"
        ],
        ["Mutation / Removal mutators (_statement_, _block_)"] =
        [
          "StatementRemovalMutationsCompileActivateAndExecuteInEngineAsync"
        ],
        ["Mutation / Unary Operators (_unary_)"] = ExpressionConformanceSuites,
        ["Mutation / Update Operators (_update_)"] = ExpressionConformanceSuites,
        ["Mutation / Checked Statements (_checked_)"] = ExpressionConformanceSuites,
        ["Mutation / Linq Methods (_linq_)"] =
        [
          "EveryDocumentedLinqTransformationCompilesAndActivatesAsync"
        ],
        ["Mutation / String Literals (_string_)"] =
        [
          "EveryDocumentedStringLiteralTransformationCompilesAndActivates",
          "EveryDocumentedStringValueTransformationExecutesAndKillsInEngineAsync"
        ],
        ["Mutation / String Methods (_stringmethod_)"] =
        [
          "EveryDocumentedStringMethodTransformationCompilesAndActivatesAsync"
        ],
        ["Mutation / Math Methods (_math_)"] =
        [
          "EveryDocumentedMathTransformationCompilesAndActivatesInItsApplicableOverloadAsync"
        ],
        ["Mutation / Null-coalescing Operators (_nullcoalescing_)"] = ExpressionConformanceSuites,
        ["Mutation / Conditional Operators (_conditional_)"] = ExpressionConformanceSuites,
        ["Mutation / Collection Expressions (_collectionexpression_)"] = ExpressionConformanceSuites
      };

  [Test]
  public void EveryPinnedTransformationHasAConsistentStatusAndConformanceSuite()
  {
    var root = FindRepositoryRoot();
    var matrixPath = Path.Combine(root, "benchmarks", "stryker-net-5.0.0-parity.json");
    using var matrix = JsonDocument.Parse(File.ReadAllText(matrixPath));
    var rows = matrix.RootElement.GetProperty("documentedTransformations").EnumerateArray().ToArray();
    var sourceDerivedCases = matrix.RootElement.GetProperty("sourceDerivedCases").EnumerateArray().ToArray();
    var summary = matrix.RootElement.GetProperty("summary");
    Assert.That(summary.GetProperty("documentedRows").GetInt32(), Is.EqualTo(rows.Length));

    var supported = rows.Count(row => row.GetProperty("status").GetString() == "supported");
    var unsupported = rows.Where(row => row.GetProperty("status").GetString() == "unsupported").ToArray();
    Assert.That(summary.GetProperty("supported").GetInt32(), Is.EqualTo(supported));
    Assert.That(summary.GetProperty("unsupported").GetInt32(), Is.EqualTo(unsupported.Length));
    Assert.That(unsupported.All(row => !string.IsNullOrWhiteSpace(row.GetProperty("reason").GetString())), Is.True,
        "Each unsupported transformation must retain its runtime limitation diagnostic.");
    Assert.That(rows.All(row => row.GetProperty("status").GetString() is "supported" or "unsupported"), Is.True);

    Assert.That(sourceDerivedCases, Is.Not.Empty, "Implementation-only behavior from the pinned Stryker source must be inventoried separately.");
    Assert.That(sourceDerivedCases.All(row => row.GetProperty("status").GetString() is "supported" or "unsupported" &&
        !string.IsNullOrWhiteSpace(row.GetProperty("source").GetString())), Is.True,
        "Each source-derived case must retain a support status and pinned implementation source path.");
    var categories = rows.Concat(sourceDerivedCases).Select(row => row.GetProperty("category").GetString()!)
        .Distinct(StringComparer.Ordinal).ToArray();
    var unmappedMutationCategories = categories.Where(category =>
        category.StartsWith("Mutation / ", StringComparison.Ordinal) &&
        !MutationConformanceSuites.ContainsKey(category)).ToArray();
    Assert.That(unmappedMutationCategories, Is.Empty,
        $"Documented mutation categories have no conformance suite: {string.Join(", ", unmappedMutationCategories)}");
    Assert.That(categories.Where(category => category.StartsWith("Regex / ", StringComparison.Ordinal)),
        Is.Not.Empty, "The pinned matrix must retain the documented regex categories.");

    var testType = typeof(MutationEngineIntegrationTests);
    var referencedSuites = MutationConformanceSuites.Values.SelectMany(suites => suites)
        .Concat(RegexConformanceSuites).Distinct(StringComparer.Ordinal).ToArray();
    var integrationSourcePath = Path.Combine(root, "tst", "MutationFramework.EngineTests", "MutationEngineIntegrationTests.cs");
    var integrationSource = CSharpSyntaxTree.ParseText(File.ReadAllText(integrationSourcePath), path: integrationSourcePath)
        .GetRoot();
    foreach (var suiteName in referencedSuites)
    {
      var suite = testType.GetMethod(suiteName, BindingFlags.Instance | BindingFlags.Public);
      Assert.That(suite, Is.Not.Null, $"Conformance suite '{suiteName}' was renamed or removed.");
      Assert.That(suite!.GetCustomAttribute<TestAttribute>(), Is.Not.Null,
          $"Conformance suite '{suiteName}' is no longer an NUnit test.");
      var declaration = integrationSource.DescendantNodes().OfType<MethodDeclarationSyntax>()
          .SingleOrDefault(method => method.Identifier.ValueText == suiteName);
      Assert.That(declaration, Is.Not.Null, $"Conformance suite '{suiteName}' has no source declaration.");
      Assert.That(declaration!.ToFullString(), Does.Contain("stryker-net-5.0.0-parity.json"),
          $"Conformance suite '{suiteName}' must consume the pinned transformation matrix.");
    }

    foreach (var (category, suites) in MutationConformanceSuites)
    {
      var categoryEvidence = category.StartsWith("Regex / ", StringComparison.Ordinal) ? "Regex /" : category;
      foreach (var suiteName in suites)
      {
        var declaration = integrationSource.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == suiteName);
        Assert.That(declaration.ToFullString(), Does.Contain(categoryEvidence),
            $"Conformance suite '{suiteName}' no longer exercises documented category '{category}'.");
      }
    }
  }

  private static string FindRepositoryRoot()
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
      if (File.Exists(Path.Combine(current.FullName, "MutationFramework.slnx")))
      {
        return current.FullName;
      }

      current = current.Parent;
    }

    throw new DirectoryNotFoundException("Unable to locate MutationFramework.slnx from the test output directory.");
  }
}
