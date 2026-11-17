using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

internal abstract class MethodNameMutator(string id, MutationCategory category, IReadOnlyDictionary<string, string[]> replacements,
    Func<IMethodSymbol, bool> methodFilter) : IMutator<InvocationExpressionSyntax>
{
  private readonly Dictionary<string, string[]> _replacements = replacements.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
  public MutatorMetadata Metadata { get; } = new(id, 1, category,
      new HashSet<string>(replacements.Keys, StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([category]) };

  public IEnumerable<MutationDescription> Generate(InvocationExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol method || !methodFilter(method) ||
        !_replacements.TryGetValue(method.Name, out var replacements))
    {
      yield break;
    }

    var result = validate(node, model);
    foreach (var replacementName in replacements)
    {
      if (node.Expression is not MemberAccessExpressionSyntax member)
      {
        continue;
      }

      var replacement = node.WithExpression(member.WithName(SyntaxFactory.IdentifierName(replacementName))).ToString();
      var replacementSyntax = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression(replacement);
      var replacementSymbol = model.GetSpeculativeSymbolInfo(node.SpanStart, replacementSyntax, SpeculativeBindingOption.BindAsExpression);
      var unsupportedReason = !result.Supported
          ? result.Reason
          : replacementSymbol.Symbol is null
              ? $"replacement method '{replacementName}' has no applicable overload for the original argument list"
              : null;
      yield return new(Metadata, node, replacement, method.Name + "->" + replacementName, node.ToString(), unsupportedReason);
    }
  }
}


internal sealed class StringMethodMutator() : MethodNameMutator("string-methods", MutationCategory.StringMethods,
    new Dictionary<string, string[]>
    {
      ["EndsWith"] = ["StartsWith"],
      ["StartsWith"] = ["EndsWith"],
      ["IndexOf"] = ["LastIndexOf"],
      ["LastIndexOf"] = ["IndexOf"],
      ["PadLeft"] = ["PadRight"],
      ["PadRight"] = ["PadLeft"],
      ["ToLower"] = ["ToUpper"],
      ["ToUpper"] = ["ToLower"],
      ["ToLowerInvariant"] = ["ToUpperInvariant"],
      ["ToUpperInvariant"] = ["ToLowerInvariant"],
      ["TrimStart"] = ["TrimEnd"],
      ["TrimEnd"] = ["TrimStart"]
    }, method => method.ContainingType.SpecialType == SpecialType.System_String)
{ }


internal sealed class LinqMethodMutator() : MethodNameMutator("linq-methods", MutationCategory.Linq,
    new Dictionary<string, string[]>
    {
      ["All"] = ["Any"],
      ["Any"] = ["All"],
      ["AsEnumerable"] = ["Reverse"],
      ["Reverse"] = ["AsEnumerable"],
      ["Average"] = ["Min"],
      ["Min"] = ["Max"],
      ["Max"] = ["Min"],
      ["Concat"] = ["Except"],
      ["Except"] = ["Concat"],
      ["Count"] = ["Sum"],
      ["Sum"] = ["Max"],
      ["First"] = ["FirstOrDefault"],
      ["FirstOrDefault"] = ["First"],
      ["Last"] = ["First"],
      ["Intersect"] = ["Union"],
      ["Union"] = ["Intersect"],
      ["IntersectBy"] = ["UnionBy"],
      ["UnionBy"] = ["IntersectBy"],
      ["MaxBy"] = ["MinBy"],
      ["MinBy"] = ["MaxBy"],
      ["Order"] = ["OrderDescending"],
      ["OrderDescending"] = ["Order"],
      ["OrderBy"] = ["OrderByDescending"],
      ["OrderByDescending"] = ["OrderBy"],
      ["Single"] = ["SingleOrDefault"],
      ["SingleOrDefault"] = ["Single"],
      ["Skip"] = ["Take"],
      ["Take"] = ["Skip"],
      ["SkipLast"] = ["TakeLast"],
      ["TakeLast"] = ["SkipLast"],
      ["SkipWhile"] = ["TakeWhile"],
      ["TakeWhile"] = ["SkipWhile"],
      ["ThenBy"] = ["ThenByDescending"],
      ["ThenByDescending"] = ["ThenBy"]
    }, method => method.ContainingNamespace.ToDisplayString().StartsWith("System.Linq", StringComparison.Ordinal) ||
        method.Name is "Max" or "Min" && method.ContainingType.ToDisplayString() == "System.MemoryExtensions")
{ }


internal sealed class MathMethodMutator() : MethodNameMutator("math-methods", MutationCategory.Math,
    new Dictionary<string, string[]>
    {
      ["Acos"] = ["Acosh", "Asin", "Atan"],
      ["Acosh"] = ["Acos", "Asinh", "Atanh"],
      ["Asin"] = ["Asinh", "Acos", "Atan"],
      ["Asinh"] = ["Asin", "Acosh", "Atanh"],
      ["Atan"] = ["Atanh", "Acos", "Asin"],
      ["Atanh"] = ["Atan", "Acosh", "Asinh"],
      ["BitDecrement"] = ["BitIncrement"],
      ["BitIncrement"] = ["BitDecrement"],
      ["Ceiling"] = ["Floor"],
      ["Floor"] = ["Ceiling"],
      ["Cos"] = ["Cosh", "Sin", "Tan"],
      ["Cosh"] = ["Cos", "Sinh", "Tanh"],
      ["Sin"] = ["Sinh", "Cos", "Tan"],
      ["Sinh"] = ["Sin", "Cosh", "Tanh"],
      ["Tan"] = ["Tanh", "Cos", "Sin"],
      ["Tanh"] = ["Tan", "Cosh", "Sinh"],
      ["Exp"] = ["Log"],
      ["Log"] = ["Exp", "Pow"],
      ["Pow"] = ["Log"],
      ["MaxMagnitude"] = ["MinMagnitude"],
      ["MinMagnitude"] = ["MaxMagnitude"],
      ["ReciprocalEstimate"] = ["ReciprocalSqrtEstimate"],
      ["ReciprocalSqrtEstimate"] = ["ReciprocalEstimate", "Sqrt"]
    }, method => method.ContainingType.ToDisplayString() is "System.Math" or "System.MathF")
{ }
