using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

internal sealed class BooleanConditionMutator : IMutator<ExpressionSyntax>
{
  public MutatorMetadata Metadata
  {
    get;
  } = new("boolean-conditions", 1, MutationCategory.Boolean,
      new HashSet<string>(["if/while/do/for conditions"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.Boolean])
  };

  public IEnumerable<MutationDescription> Generate(ExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (node.Parent is not (IfStatementSyntax or WhileStatementSyntax or DoStatementSyntax) &&
        !(node.Parent is ForStatementSyntax forStatement && forStatement.Condition == node))
    {
      yield break;
    }
    var result = validate(node, model);
    yield return new(Metadata, node, $"!({node})", "boolean:negate-condition", node.ToString(), result.Supported ? null : result.Reason);
  }
}

internal sealed class CheckedExpressionMutator : IMutator<CheckedExpressionSyntax>
{
  public MutatorMetadata Metadata
  {
    get;
  } = new("checked-contexts", 1, MutationCategory.Checked,
      new HashSet<string>(["checked expression", "unchecked expression"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.Checked])
  };

  public IEnumerable<MutationDescription> Generate(CheckedExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    var result = validate(node, model);
    if (result.Reason is "explicit checked/unchecked arithmetic context cannot be preserved by expression instrumentation" or "Roslyn could not bind the expression")
    {
      result = (true, null);
    }
    yield return new(Metadata, node, node.Expression.ToString(), "checked:remove", node.ToString(), result.Supported ? null : result.Reason);
  }
}

internal sealed class CollectionExpressionMutator : IMutator<CollectionExpressionSyntax>
{
  public MutatorMetadata Metadata
  {
    get;
  } = new("collection-expressions", 1, MutationCategory.CollectionExpression,
      new HashSet<string>(["C# 12 collection expression"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.CollectionExpression])
  };

  public IEnumerable<MutationDescription> Generate(CollectionExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    var result = validate(node, model);
    var replacement = node.Elements.Count == 0 ? "[default]" : "[]";
    yield return new(Metadata, node, replacement, node.Elements.Count == 0 ? "collection:add-default" : "collection:empty", node.ToString(), result.Supported ? null : result.Reason);
  }
}

internal sealed class UnaryOperatorMutator : IMutator<PrefixUnaryExpressionSyntax>, IMutator<PostfixUnaryExpressionSyntax>
{
  private static readonly Dictionary<SyntaxKind, string> Prefix = new()
  {
    [SyntaxKind.UnaryMinusExpression] = "+",
    [SyntaxKind.UnaryPlusExpression] = "-",
    [SyntaxKind.BitwiseNotExpression] = "",
    [SyntaxKind.LogicalNotExpression] = "",
    [SyntaxKind.PreIncrementExpression] = "--",
    [SyntaxKind.PreDecrementExpression] = "++"
  };
  private static readonly Dictionary<SyntaxKind, string> Postfix = new()
  {
    [SyntaxKind.PostIncrementExpression] = "--",
    [SyntaxKind.PostDecrementExpression] = "++"
  };

  public MutatorMetadata Metadata
  {
    get;
  } = new("unary-update", 1, MutationCategory.Unary,
      new HashSet<string>(["unary +/-", "bitwise complement", "prefix/postfix increment/decrement"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.Unary, MutationCategory.Update])
  };

  IEnumerable<MutationDescription> IMutator.Generate(SyntaxNode node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate) => node switch
      {
        PrefixUnaryExpressionSyntax prefix => Generate(prefix, model, validate),
        PostfixUnaryExpressionSyntax postfix => Generate(postfix, model, validate),
        _ => []
      };

  public IEnumerable<MutationDescription> Generate(PrefixUnaryExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (!Prefix.TryGetValue(node.Kind(), out var replacement))
    {
      yield break;
    }

    var text = replacement.Length == 0 ? $"({node.Operand})" : $"{replacement}({node.Operand})";
    var result = validate(node, model);
    var category = node.IsKind(SyntaxKind.PreIncrementExpression) || node.IsKind(SyntaxKind.PreDecrementExpression)
        ? MutationCategory.Update
        : node.IsKind(SyntaxKind.LogicalNotExpression) ? MutationCategory.Boolean : MutationCategory.Unary;
    yield return new(Metadata with
    {
      Category = category
    },
        node, text, "unary:" + replacement, node.ToString(), result.Supported ? null : result.Reason);
  }

  public IEnumerable<MutationDescription> Generate(PostfixUnaryExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (!Postfix.TryGetValue(node.Kind(), out var replacement))
    {
      yield break;
    }

    var result = validate(node, model);
    yield return new(Metadata with
    {
      Category = MutationCategory.Update
    }, node, $"{node.Operand}{replacement}",
        "unary:" + replacement, node.ToString(), result.Supported ? null : result.Reason);
  }
}

internal sealed class ConditionalExpressionMutator : IMutator<ConditionalExpressionSyntax>
{
  public MutatorMetadata Metadata
  {
    get;
  } = new("conditional-expressions", 1, MutationCategory.Conditional,
      new HashSet<string>(["conditional operator"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.Conditional])
  };

  public IEnumerable<MutationDescription> Generate(ConditionalExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    var result = validate(node, model);
    foreach (var (value, token) in new[] { ("true", "conditional:true"), ("false", "conditional:false") })
    {
      yield return new(Metadata, node, $"{value} ? ({node.WhenTrue}) : ({node.WhenFalse})", token, node.ToString(), result.Supported ? null : result.Reason);
    }
  }
}

internal sealed class NullCoalescingMutator : IMutator<BinaryExpressionSyntax>
{
  public MutatorMetadata Metadata
  {
    get;
  } = new("null-coalescing", 1, MutationCategory.NullCoalescing,
      new HashSet<string>(["?? expression"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.NullCoalescing])
  };

  public IEnumerable<MutationDescription> Generate(BinaryExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (!node.IsKind(SyntaxKind.CoalesceExpression))
    {
      yield break;
    }

    var result = validate(node, model);
    yield return new(Metadata, node, $"({node.Right}) ?? ({node.Left})", "coalesce:swap", node.ToString(), result.Supported ? null : result.Reason);
    yield return new(Metadata, node, node.Left.ToString(), "coalesce:left", node.ToString(), result.Supported ? null : result.Reason);
    yield return new(Metadata, node, node.Right.ToString(), "coalesce:right", node.ToString(), result.Supported ? null : result.Reason);
  }
}

internal sealed class BooleanLiteralMutator : IMutator<LiteralExpressionSyntax>
{
  public MutatorMetadata Metadata
  {
    get;
  } = new("boolean-literals", 1, MutationCategory.Boolean,
      new HashSet<string>(["true literal", "false literal"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.Boolean])
  };

  public IEnumerable<MutationDescription> Generate(LiteralExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    var literal = node;
    if (!(literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
    {
      yield break;
    }

    var validity = validate(literal, model);
    var replacement = literal.IsKind(SyntaxKind.TrueLiteralExpression) ? "false" : "true";
    yield return new(Metadata, literal, replacement, replacement, literal.ToString(), validity.Supported ? null : validity.Reason);
  }
}
