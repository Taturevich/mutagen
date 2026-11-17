using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

internal sealed class AssignmentExpressionMutator : IMutator<AssignmentExpressionSyntax>
{
  private static readonly Dictionary<SyntaxKind, (SyntaxKind Kind, string Token)[]> Operators =
      new Dictionary<SyntaxKind, (SyntaxKind, string)[]>
      {
        [SyntaxKind.AddAssignmentExpression] = [(SyntaxKind.SubtractAssignmentExpression, "-=")],
        [SyntaxKind.SubtractAssignmentExpression] = [(SyntaxKind.AddAssignmentExpression, "+=")],
        [SyntaxKind.MultiplyAssignmentExpression] = [(SyntaxKind.DivideAssignmentExpression, "/=")],
        [SyntaxKind.DivideAssignmentExpression] = [(SyntaxKind.MultiplyAssignmentExpression, "*=")],
        [SyntaxKind.ModuloAssignmentExpression] = [(SyntaxKind.MultiplyAssignmentExpression, "*=")],
        [SyntaxKind.AndAssignmentExpression] = [(SyntaxKind.OrAssignmentExpression, "|="), (SyntaxKind.ExclusiveOrAssignmentExpression, "^=")],
        [SyntaxKind.OrAssignmentExpression] = [(SyntaxKind.AndAssignmentExpression, "&="), (SyntaxKind.ExclusiveOrAssignmentExpression, "^=")],
        [SyntaxKind.ExclusiveOrAssignmentExpression] = [(SyntaxKind.OrAssignmentExpression, "|="), (SyntaxKind.AndAssignmentExpression, "&=")],
        [SyntaxKind.LeftShiftAssignmentExpression] = [(SyntaxKind.RightShiftAssignmentExpression, ">>="), (SyntaxKind.UnsignedRightShiftAssignmentExpression, ">>>=")],
        [SyntaxKind.RightShiftAssignmentExpression] = [(SyntaxKind.LeftShiftAssignmentExpression, "<<="), (SyntaxKind.UnsignedRightShiftAssignmentExpression, ">>>=")],
        [SyntaxKind.CoalesceAssignmentExpression] = [(SyntaxKind.SimpleAssignmentExpression, "=")],
        [SyntaxKind.UnsignedRightShiftAssignmentExpression] = [(SyntaxKind.LeftShiftAssignmentExpression, "<<="), (SyntaxKind.RightShiftAssignmentExpression, ">>=")]
      };

  public MutatorMetadata Metadata { get; } = new("assignment-expressions", 1, MutationCategory.Assignment,
      new HashSet<string>(["compound assignment", "coalescing assignment"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.Assignment])
  };

  public IEnumerable<MutationDescription> Generate(AssignmentExpressionSyntax node, SemanticModel semanticModel,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (!Operators.TryGetValue(node.Kind(), out var replacements))
    {
      yield break;
    }

    if (node.IsKind(SyntaxKind.AddAssignmentExpression) &&
        (semanticModel.GetTypeInfo(node.Left).Type?.SpecialType == SpecialType.System_String ||
         semanticModel.GetTypeInfo(node.Right).Type?.SpecialType == SpecialType.System_String))
    {
      yield break;
    }

    var validity = validate(node, semanticModel);
    foreach (var (_, token) in replacements)
    {
      yield return new(Metadata, node, $"{node.Left} {token} ({node.Right})", token,
          node.ToString(), validity.Supported ? null : validity.Reason);
    }
  }
}


internal sealed class BinaryOperatorMutator : IMutator<BinaryExpressionSyntax>
{
  private static readonly IReadOnlyDictionary<SyntaxKind, (MutationCategory Category, SyntaxKind Replacement, bool Invert)[]> Operators =
      new Dictionary<SyntaxKind, (MutationCategory, SyntaxKind, bool)[]>
      {
        [SyntaxKind.AddExpression] = [(MutationCategory.Arithmetic, SyntaxKind.SubtractExpression, false)],
        [SyntaxKind.SubtractExpression] = [(MutationCategory.Arithmetic, SyntaxKind.AddExpression, false)],
        [SyntaxKind.MultiplyExpression] = [(MutationCategory.Arithmetic, SyntaxKind.DivideExpression, false)],
        [SyntaxKind.DivideExpression] = [(MutationCategory.Arithmetic, SyntaxKind.MultiplyExpression, false)],
        [SyntaxKind.ModuloExpression] = [(MutationCategory.Arithmetic, SyntaxKind.MultiplyExpression, false)],
        [SyntaxKind.GreaterThanExpression] = [(MutationCategory.Equality, SyntaxKind.LessThanExpression, false), (MutationCategory.Equality, SyntaxKind.GreaterThanOrEqualExpression, false)],
        [SyntaxKind.GreaterThanOrEqualExpression] = [(MutationCategory.Equality, SyntaxKind.LessThanExpression, false), (MutationCategory.Equality, SyntaxKind.GreaterThanExpression, false)],
        [SyntaxKind.LessThanExpression] = [(MutationCategory.Equality, SyntaxKind.GreaterThanExpression, false), (MutationCategory.Equality, SyntaxKind.LessThanOrEqualExpression, false)],
        [SyntaxKind.LessThanOrEqualExpression] = [(MutationCategory.Equality, SyntaxKind.GreaterThanExpression, false), (MutationCategory.Equality, SyntaxKind.LessThanExpression, false)],
        [SyntaxKind.EqualsExpression] = [(MutationCategory.Equality, SyntaxKind.NotEqualsExpression, false)],
        [SyntaxKind.NotEqualsExpression] = [(MutationCategory.Equality, SyntaxKind.EqualsExpression, false)],
        [SyntaxKind.LogicalAndExpression] = [(MutationCategory.Logical, SyntaxKind.LogicalOrExpression, false)],
        [SyntaxKind.LogicalOrExpression] = [(MutationCategory.Logical, SyntaxKind.LogicalAndExpression, false)],
        [SyntaxKind.BitwiseAndExpression] = [(MutationCategory.Bitwise, SyntaxKind.BitwiseOrExpression, false)],
        [SyntaxKind.BitwiseOrExpression] = [(MutationCategory.Bitwise, SyntaxKind.BitwiseAndExpression, false)],
        [SyntaxKind.LeftShiftExpression] = [(MutationCategory.Bitwise, SyntaxKind.RightShiftExpression, false), (MutationCategory.Bitwise, SyntaxKind.UnsignedRightShiftExpression, false)],
        [SyntaxKind.RightShiftExpression] = [(MutationCategory.Bitwise, SyntaxKind.LeftShiftExpression, false), (MutationCategory.Bitwise, SyntaxKind.UnsignedRightShiftExpression, false)],
        [SyntaxKind.UnsignedRightShiftExpression] = [(MutationCategory.Bitwise, SyntaxKind.LeftShiftExpression, false), (MutationCategory.Bitwise, SyntaxKind.RightShiftExpression, false)]
      };

  public MutatorMetadata Metadata { get; } = new("binary-operators", 1, MutationCategory.Arithmetic,
      new HashSet<string>(["binary expressions", "arithmetic", "relational", "equality", "conditional logical", "bitwise", "shift"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.Arithmetic, MutationCategory.Equality, MutationCategory.Logical, MutationCategory.Bitwise])
  };

  public IEnumerable<MutationDescription> Generate(BinaryExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    var binary = node;
    var replacements = Operators.GetValueOrDefault(binary.Kind(), []);
    if (binary.IsKind(SyntaxKind.ExclusiveOrExpression))
    {
      var resultType = model.GetTypeInfo(binary).Type;
      replacements = resultType?.SpecialType == SpecialType.System_Boolean
          ? [(MutationCategory.Logical, SyntaxKind.EqualsExpression, false)]
          : [(MutationCategory.Bitwise, SyntaxKind.ExclusiveOrExpression, true)];
    }
    if (replacements.Length == 0)
    {
      yield break;
    }

    var validity = validate(binary, model);
    foreach (var (category, kind, invert) in replacements)
    {
      var token = kind switch
      {
        SyntaxKind.AddExpression => "+",
        SyntaxKind.SubtractExpression => "-",
        SyntaxKind.MultiplyExpression => "*",
        SyntaxKind.DivideExpression => "/",
        SyntaxKind.GreaterThanExpression => ">",
        SyntaxKind.GreaterThanOrEqualExpression => ">=",
        SyntaxKind.LessThanExpression => "<",
        SyntaxKind.LessThanOrEqualExpression => "<=",
        SyntaxKind.EqualsExpression => "==",
        SyntaxKind.NotEqualsExpression => "!=",
        SyntaxKind.LogicalAndExpression => "&&",
        SyntaxKind.LogicalOrExpression => "||",
        SyntaxKind.BitwiseAndExpression => "&",
        SyntaxKind.BitwiseOrExpression => "|",
        SyntaxKind.LeftShiftExpression => "<<",
        SyntaxKind.RightShiftExpression => ">>",
        SyntaxKind.UnsignedRightShiftExpression => ">>>",
        SyntaxKind.ExclusiveOrExpression => "^",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
      };
      if (invert)
      {
        token = "~^";
      }

      var replacement = invert
          ? $"~(({binary.Left}) ^ ({binary.Right}))"
          : SyntaxFactory.ParseExpression($"({binary.Left}) {token} ({binary.Right})").ToString();
      yield return new(Metadata with { Category = category }, binary,
          replacement, token,
          binary.ToString(), validity.Supported ? null : validity.Reason);
    }
  }
}
