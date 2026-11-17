using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

/// <summary>Stable categories used by configuration and versioned reports.</summary>
public enum MutationCategory
{
  Arithmetic, Equality, Logical, Boolean, Assignment, Initialization, Removal, Unary,
  Update, Checked, Linq, String, StringMethods, Bitwise, Regex, Math, NullCoalescing,
  Conditional, CollectionExpression
}

/// <summary>Describes how the schema builder applies an extension mutation.</summary>
public enum MutationApplicationKind
{
  BuiltIn, ExpressionReplacement, StatementReplacement
}

public sealed record MutatorMetadata(string Id, int SchemaVersion, MutationCategory Category,
    IReadOnlySet<string> SupportedConstructs)
{
  public IReadOnlySet<MutationCategory> Categories { get; init; } = new HashSet<MutationCategory>();
}

/// <summary>A mutation description is data; it never edits source.</summary>
public sealed record MutationDescription(MutatorMetadata Mutator, SyntaxNode Node,
    string Replacement, string ReplacementToken, string Original, string? UnsupportedReason = null)
{
  public MutationApplicationKind ApplicationKind { get; init; } = MutationApplicationKind.BuiltIn;
}

/// <summary>Implementations inspect typed Roslyn nodes and semantic context, then describe candidates.</summary>
public interface IMutator
{
  MutatorMetadata Metadata { get; }
  IEnumerable<MutationDescription> Generate(SyntaxNode node, SemanticModel semanticModel,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate);
}

public interface IMutator<TNode> : IMutator where TNode : SyntaxNode
{
  IEnumerable<MutationDescription> Generate(TNode node, SemanticModel semanticModel,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate);

  IEnumerable<MutationDescription> IMutator.Generate(SyntaxNode node, SemanticModel semanticModel,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate) =>
      node is TNode typed ? Generate(typed, semanticModel, validate) : [];
}
