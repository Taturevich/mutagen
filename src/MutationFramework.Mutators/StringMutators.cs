using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

internal sealed class StringConstantMutator : IMutator<InvocationExpressionSyntax>
{
  public MutatorMetadata Metadata
  {
    get;
  } = new("string-method-constants", 1, MutationCategory.StringMethods,
      new HashSet<string>(["Substring", "Trim", "ElementAt", "ElementAtOrDefault"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.StringMethods])
  };

  public IEnumerable<MutationDescription> Generate(InvocationExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol method)
    {
      yield break;
    }

    var replacement = method.ContainingType.SpecialType == SpecialType.System_String && (method.Name is "Substring" or "Trim")
        ? "\"\""
        : (method.Name is "ElementAt" or "ElementAtOrDefault") && method.ReturnType.SpecialType == SpecialType.System_Char &&
          method.ContainingNamespace.ToDisplayString().StartsWith("System.Linq", StringComparison.Ordinal) ? "'\\0'" : null;
    if (replacement is null)
    {
      yield break;
    }

    var result = validate(node, model);
    yield return new(Metadata, node, replacement, method.Name + "->constant", node.ToString(), result.Supported ? null : result.Reason);
  }
}

internal sealed class StringValueMutator : IMutator<LiteralExpressionSyntax>, IMutator<InterpolatedStringExpressionSyntax>, IMutator<MemberAccessExpressionSyntax>, IMutator<InvocationExpressionSyntax>
{
  private static readonly Dictionary<string, string[]> NullChecks = new(StringComparer.Ordinal)
  {
    ["IsNullOrEmpty"] = ["{0} != null", "{0} != \"\""],
    ["IsNullOrWhiteSpace"] = ["{0} != null", "{0} != \"\"", "{0}.Trim() != \"\""]
  };
  public MutatorMetadata Metadata
  {
    get;
  } = new("string-values", 1, MutationCategory.String,
      new HashSet<string>(["string literals", "interpolated strings", "string.Empty", "null/empty checks"], StringComparer.Ordinal))
  {
    Categories = new HashSet<MutationCategory>([MutationCategory.String])
  };

  IEnumerable<MutationDescription> IMutator.Generate(SyntaxNode node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate) => node switch
      {
        LiteralExpressionSyntax literal => Generate(literal, model, validate),
        InterpolatedStringExpressionSyntax interpolated => Generate(interpolated, model, validate),
        MemberAccessExpressionSyntax member => Generate(member, model, validate),
        InvocationExpressionSyntax invocation => Generate(invocation, model, validate),
        _ => []
      };

  public IEnumerable<MutationDescription> Generate(LiteralExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (!node.IsKind(SyntaxKind.StringLiteralExpression))
    {
      yield break;
    }

    var replacement = node.Token.ValueText.Length == 0
        ? "\"Stryker was here!\""
        : node.Token.Text.StartsWith("@\"", StringComparison.Ordinal) ? "@\"\"" : "\"\"";
    var result = validate(node, model);
    yield return new(Metadata, node, replacement, "string:literal:" + replacement, node.ToString(), result.Supported ? null : result.Reason);
  }

  public IEnumerable<MutationDescription> Generate(InterpolatedStringExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (!node.Contents.OfType<InterpolatedStringTextSyntax>().Any(text => text.TextToken.ValueText.Length > 0))
    {
      yield break;
    }

    var result = validate(node, model);
    yield return new(Metadata, node, "$\"\"", "string:interpolated-empty", node.ToString(), result.Supported ? null : result.Reason);
  }

  public IEnumerable<MutationDescription> Generate(MemberAccessExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (node.Name.Identifier.ValueText != "Empty" || model.GetSymbolInfo(node).Symbol is not IFieldSymbol field ||
        field.ContainingType.SpecialType != SpecialType.System_String)
    {
      yield break;
    }

    var result = validate(node, model);
    yield return new(Metadata, node, "\"Stryker was here!\"", "string:empty-field", node.ToString(), result.Supported ? null : result.Reason);
  }

  public IEnumerable<MutationDescription> Generate(InvocationExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol method || method.ContainingType.SpecialType != SpecialType.System_String ||
        !NullChecks.TryGetValue(method.Name, out var replacements) || node.ArgumentList.Arguments.Count != 1)
    {
      yield break;
    }

    var argument = node.ArgumentList.Arguments[0].Expression.ToString();
    var result = validate(node, model);
    foreach (var replacement in replacements)
    {
      var expression = $"({string.Format(System.Globalization.CultureInfo.InvariantCulture, replacement, argument)})";
      yield return new(Metadata, node, expression, "string:null-check:" + replacement, node.ToString(), result.Supported ? null : result.Reason);
    }
  }
}
