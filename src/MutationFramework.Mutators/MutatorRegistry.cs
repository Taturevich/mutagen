using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

/// <summary>Explicit, deterministic registry. No reflection or runtime assembly scanning is used.</summary>
public sealed class MutatorRegistry
{
  private readonly IReadOnlyList<IMutator> _mutators;
  public string Fingerprint
  {
    get;
  }

  public MutatorRegistry(IEnumerable<IMutator> mutators, IReadOnlySet<string>? selectedIds = null)
  {
    ArgumentNullException.ThrowIfNull(mutators);
    var all = mutators.ToArray();
    if (all.Any(x => x is null))
    {
      throw new ArgumentException("Mutator registrations cannot contain null.", nameof(mutators));
    }

    if (all.Select(x => x.Metadata.Id).Distinct(StringComparer.Ordinal).Count() != all.Length)
    {
      throw new ArgumentException("Mutator IDs must be unique.", nameof(mutators));
    }

    if (selectedIds is not null)
    {
      var unknown = selectedIds.Except(all.Select(x => x.Metadata.Id), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
      if (unknown.Length != 0)
      {
        throw new ArgumentException($"Unknown mutator IDs: {string.Join(", ", unknown)}", nameof(selectedIds));
      }

      all = all.Where(x => selectedIds.Contains(x.Metadata.Id)).ToArray();
    }
    _mutators = all.OrderBy(x => x.Metadata.Id, StringComparer.Ordinal).ToArray();
    Fingerprint = string.Join(";", _mutators.Select(mutator =>
        $"{mutator.Metadata.Id}:{mutator.Metadata.SchemaVersion}:{mutator.GetType().Assembly.ManifestModule.ModuleVersionId:N}"));
  }

  public IReadOnlyList<MutatorMetadata> Mutators => _mutators.Select(x => x.Metadata).ToArray();

  public IEnumerable<MutationDescription> Generate(SyntaxNode node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    foreach (var mutator in _mutators)
    {
      foreach (var description in mutator.Generate(node, model, validate))
      {
        yield return description;
      }
    }
  }

  public static MutatorRegistry CreateDefault(IReadOnlySet<string>? selectedIds = null) =>
      new([new AssignmentExpressionMutator(), new BinaryOperatorMutator(), new BooleanLiteralMutator(),
          new UnaryOperatorMutator(), new ConditionalExpressionMutator(), new NullCoalescingMutator(),
          new CheckedExpressionMutator(), new CollectionExpressionMutator(), new InitializationMutator(), new RemovalMutator(), new RegexMutator(), new BooleanConditionMutator(),
          new IsPatternMutator(), new BinaryPatternMutator(), new StringValueMutator(), new StringMethodMutator(),
          new StringConstantMutator(), new LinqMethodMutator(), new MathMethodMutator()], selectedIds);
}
