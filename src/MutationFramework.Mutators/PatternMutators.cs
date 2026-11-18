using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

internal sealed class IsPatternMutator : IMutator<IsPatternExpressionSyntax>
{
  public MutatorMetadata Metadata
  {
    get;
  } = new("is-patterns", 1, MutationCategory.Equality,
      new HashSet<string>(["is pattern", "is not pattern"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.Equality])
  };

  public IEnumerable<MutationDescription> Generate(IsPatternExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    var pattern = node.Pattern is UnaryPatternSyntax unary && unary.IsKind(SyntaxKind.NotPattern) ? unary.Pattern : node.Pattern;
    var replacement = node.Pattern is UnaryPatternSyntax negative && negative.IsKind(SyntaxKind.NotPattern)
        ? $"{node.Expression} is {pattern}"
        : $"{node.Expression} is not {pattern}";
    var result = validate(node, model);
    yield return new(Metadata, node, replacement, "pattern:toggle-not", node.ToString(), result.Supported ? null : result.Reason);
  }
}

internal sealed class BinaryPatternMutator : IMutator<BinaryPatternSyntax>
{
  public MutatorMetadata Metadata
  {
    get;
  } = new("binary-patterns", 1, MutationCategory.Logical,
      new HashSet<string>(["and pattern", "or pattern"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.Logical])
  };

  public IEnumerable<MutationDescription> Generate(BinaryPatternSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (!node.IsKind(SyntaxKind.AndPattern) && !node.IsKind(SyntaxKind.OrPattern))
    {
      yield break;
    }

    var owner = node.Ancestors().OfType<IsPatternExpressionSyntax>().FirstOrDefault();
    if (owner is null)
    {
      yield break;
    }

    var token = node.IsKind(SyntaxKind.AndPattern) ? "or" : "and";
    var parsedPattern = SyntaxFactory.ParseExpression($"_mutagenPatternSubject is {node.Left} {token} {node.Right}") as IsPatternExpressionSyntax;
    if (parsedPattern is null)
    {
      yield break;
    }

    var replacement = $"{owner.Expression} is {parsedPattern.Pattern}";
    var result = validate(owner, model);
    yield return new(Metadata, owner, replacement, "pattern:" + token, owner.ToString(), result.Supported ? null : result.Reason);
  }
}
