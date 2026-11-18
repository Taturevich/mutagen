using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using MutationFramework.Sdk;

using NUnit.Framework;

using static MutationFramework.EngineTests.TestWorkspace;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class CompilationRecoveryTests
{
  [TestCase(false)]
  [TestCase(true)]
  [Category("EndToEnd")]
  public async Task RecoveryRetainsCompileErrorsAndExecutesRemainingCandidates(bool execute)
  {
    var output = NewOutputDirectory();
    var options = new MutationOptions
    {
      ProjectPath = AppProject,
      TestProjects = [TestProject],
      Registry = new MutatorRegistry([new RecoveryMutator()]),
      TestFilter = "Add_ReturnsCorrectSum,IsAdult_HandlesAgeBoundary",
      MaxWorkers = 1,
      OutputDirectory = output,
      TestHostIsolationPolicy = TestHostIsolationPolicy.PerTest
    };
    try
    {
      var discovered = await MutationEngine.DiscoverAsync(options);
      Assert.That(discovered.Candidates, Has.Count.EqualTo(2));
      var result = execute ? await MutationEngine.RunAsync(options) : await MutationEngine.PlanAsync(options);
      Assert.That(result.Mutants.Select(mutant => mutant.Id), Is.EquivalentTo(discovered.Candidates.Select(candidate => candidate.Id)));
      Assert.That(result.Mutants.Count(mutant => mutant.Status == MutationStatus.CompileError), Is.EqualTo(1));
      Assert.That(result.Mutants.Count(mutant => mutant.Status == (execute ? MutationStatus.Killed : MutationStatus.Pending)), Is.EqualTo(1));
      Assert.That(result.Diagnostics, Has.Some.StartsWith("CompileError:"));
    }
    finally
    {
      if (Directory.Exists(output))
      {
        Directory.Delete(output, true);
      }
    }
  }

  private sealed class RecoveryMutator : IMutator<BinaryExpressionSyntax>
  {
    public MutatorMetadata Metadata { get; } = new("recovery-test", 1, MutationCategory.Arithmetic, new HashSet<string>(["addition"]));

    public IEnumerable<MutationDescription> Generate(BinaryExpressionSyntax node, SemanticModel model,
        Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
    {
      var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
      if (method == "Add" && node.IsKind(SyntaxKind.AddExpression))
      {
        yield return new(Metadata, node, "UnknownMutationReplacement()", "invalid", node.ToString())
        {
          ApplicationKind = MutationApplicationKind.ExpressionReplacement
        };
      }
      if (method == "IsAdult")
      {
        yield return new(Metadata, node, "false", "false", node.ToString())
        {
          ApplicationKind = MutationApplicationKind.ExpressionReplacement
        };
      }
    }
  }
}
