using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Security.Cryptography;
using System.Text;

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

/// <summary>Explicit, deterministic registry. No reflection or runtime assembly scanning is used.</summary>
public sealed class MutatorRegistry
{
  private readonly IReadOnlyList<IMutator> _mutators;
  public string Fingerprint { get; }

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

internal sealed class RegexMutator : IMutator<ObjectCreationExpressionSyntax>
{
  private static readonly Dictionary<char, char> ShorthandOpposites = new()
  {
    ['d'] = 'D',
    ['D'] = 'd',
    ['w'] = 'W',
    ['W'] = 'w',
    ['s'] = 'S',
    ['S'] = 's'
  };

  public MutatorMetadata Metadata { get; } = new("regex-patterns", 1, MutationCategory.Regex,
      new HashSet<string>(["anchors", "character classes", "quantifiers", "lookarounds", "capture groups"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.Regex]) };

  public IEnumerable<MutationDescription> Generate(ObjectCreationExpressionSyntax node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol constructor ||
        constructor.MethodKind != MethodKind.Constructor ||
        constructor.ContainingType.ToDisplayString() != "System.Text.RegularExpressions.Regex")
    {
      yield break;
    }

    var patternArgument = node.ArgumentList?.Arguments.FirstOrDefault(argument =>
        argument.NameColon?.Name.Identifier.ValueText == "pattern") ?? node.ArgumentList?.Arguments.FirstOrDefault();
    if (patternArgument is null)
    {
      yield break;
    }

    var constant = model.GetConstantValue(patternArgument.Expression);
    if (!constant.HasValue || constant.Value is not string pattern)
    {
      yield return new(Metadata, node, node.ToString(), "regex:unsupported-pattern-expression", node.ToString(),
          "Regex mutation requires a compile-time constant string pattern.");
      yield break;
    }

    string? invalidPatternMessage = null;
    try
    {
      _ = new System.Text.RegularExpressions.Regex(pattern);
    }
    catch (ArgumentException exception)
    {
      invalidPatternMessage = exception.Message;
    }
    if (invalidPatternMessage is not null)
    {
      yield return new(Metadata, node, node.ToString(), "regex:invalid-runtime-pattern", node.ToString(),
          $"The original pattern is invalid in this .NET regex runtime: {invalidPatternMessage}");
      yield break;
    }

    var emitted = new HashSet<string>(StringComparer.Ordinal);
    foreach (var (replacementPattern, token) in MutatePattern(pattern))
    {
      if (replacementPattern == pattern || !emitted.Add(replacementPattern))
      {
        continue;
      }

      try
      {
        _ = new System.Text.RegularExpressions.Regex(replacementPattern);
      }
      catch (ArgumentException)
      {
        continue;
      }

      var literal = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(replacementPattern));
      var replacement = node.ReplaceNode(patternArgument.Expression, literal).ToString();
      yield return new(Metadata, node, replacement, token + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(replacementPattern))), node.ToString());
    }
  }

  private static IEnumerable<(string Pattern, string Token)> MutatePattern(string pattern)
  {
    var emitted = new HashSet<(int Start, int Length, string Replacement)>(new RegexEditComparer());
    for (var index = 0; index < pattern.Length; index++)
    {
      var current = pattern[index];
      if (current == '[' && !IsEscaped(pattern, index) && TryFindCharacterClassEnd(pattern, index, out var classEnd))
      {
        foreach (var edit in MutateCharacterClass(pattern, index, classEnd))
        {
          if (emitted.Add((edit.Start, edit.Length, edit.Replacement)))
          {
            yield return Apply(pattern, edit.Start, edit.Length, edit.Replacement, edit.Name);
          }
        }
        index = classEnd;
        continue;
      }

      if (current == '\\' && index + 1 < pattern.Length)
      {
        var escaped = pattern[index + 1];
        if (ShorthandOpposites.TryGetValue(escaped, out var opposite))
        {
          yield return Apply(pattern, index, 2, "\\" + opposite, "regex:shorthand-negation");
          yield return Apply(pattern, index, 2, escaped.ToString(), "regex:shorthand-nullification");
          yield return Apply(pattern, index, 2, $"[\\{escaped}\\{opposite}]", "regex:shorthand-any-character");
        }
        else if (escaped is 'p' or 'P' && index + 2 < pattern.Length && pattern[index + 2] == '{')
        {
          var end = pattern.IndexOf('}', index + 3);
          if (end > index)
          {
            var flipped = escaped == 'p' ? 'P' : 'p';
            var unicode = "\\" + flipped + pattern[(index + 2)..(end + 1)];
            yield return Apply(pattern, index, end - index + 1, unicode, "regex:unicode-class-negation");
            index = end;
            continue;
          }
        }
        else if (escaped is 'A' or 'Z' or 'z' or 'b' or 'B' or 'G')
        {
          yield return Apply(pattern, index, 2, string.Empty, "regex:anchor-removal");
        }
        index++;
        continue;
      }

      if (current == '^' && index == 0 || current == '$' && index == pattern.Length - 1)
      {
        yield return Apply(pattern, index, 1, string.Empty, "regex:anchor-removal");
      }

      if (current == '(' && index + 2 < pattern.Length)
      {
        if (pattern.AsSpan(index).StartsWith("(?="))
        {
          yield return Apply(pattern, index, 3, "(?!", "regex:lookaround-negation");
        }
        else if (pattern.AsSpan(index).StartsWith("(?!"))
        {
          yield return Apply(pattern, index, 3, "(?=", "regex:lookaround-negation");
        }
        else if (pattern.AsSpan(index).StartsWith("(?<="))
        {
          yield return Apply(pattern, index, 4, "(?<!", "regex:lookaround-negation");
        }
        else if (pattern.AsSpan(index).StartsWith("(?<!"))
        {
          yield return Apply(pattern, index, 4, "(?<=", "regex:lookaround-negation");
        }
        else if (pattern[index + 1] != '?')
        {
          yield return Apply(pattern, index, 1, "(?:", "regex:group-to-non-capturing");
        }
      }

      if (current is '*' or '+' or '?' && !IsEscaped(pattern, index) &&
          !(current == '?' && index > 0 && pattern[index - 1] is '(' or '*' or '+' or '?'))
      {
        yield return Apply(pattern, index, 1, string.Empty, "regex:quantifier-removal");
        var shortRange = current switch
        {
          '?' => new[] { "{1,1}", "{0,0}", "{0,2}" },
          '*' => ["{1,}"],
          '+' => ["{0,}", "{2,}"],
          _ => []
        };
        foreach (var replacement in shortRange)
        {
          yield return Apply(pattern, index, 1, replacement, "regex:short-quantifier-range");
        }
        if (index + 1 >= pattern.Length || pattern[index + 1] != '?')
        {
          yield return Apply(pattern, index + 1, 0, "?", "regex:reluctant-quantifier");
        }
      }

      if (current == '{' && !IsEscaped(pattern, index) && TryParseRangeQuantifier(pattern, index, out var rangeEnd, out var minimum, out var maximum))
      {
        var length = rangeEnd - index + 1;
        yield return Apply(pattern, index, length, string.Empty, "regex:quantifier-removal");
        if (maximum is null)
        {
          if (minimum > 0)
          {
            yield return Apply(pattern, index, length, "{" + (minimum - 1) + ",}", "regex:quantifier-boundary");
          }
          yield return Apply(pattern, index, length, "{" + (minimum + 1) + ",}", "regex:quantifier-boundary");
        }
        else if (minimum < maximum)
        {
          if (minimum > 0)
          {
            yield return Apply(pattern, index, length, "{" + (minimum - 1) + "," + maximum + "}", "regex:quantifier-boundary");
          }
          yield return Apply(pattern, index, length, "{" + (minimum + 1) + "," + maximum + "}", "regex:quantifier-boundary");
          if (maximum - 1 >= minimum)
          {
            yield return Apply(pattern, index, length, "{" + minimum + "," + (maximum - 1) + "}", "regex:quantifier-boundary");
          }
          yield return Apply(pattern, index, length, "{" + minimum + "," + (maximum + 1) + "}", "regex:quantifier-boundary");
        }
        if (rangeEnd + 1 >= pattern.Length || pattern[rangeEnd + 1] != '?')
        {
          yield return Apply(pattern, rangeEnd + 1, 0, "?", "regex:reluctant-quantifier");
        }
        index = rangeEnd;
      }
    }
  }

  private static IEnumerable<(int Start, int Length, string Replacement, string Name)> MutateCharacterClass(string pattern, int start, int end)
  {
    var innerStart = start + 1;
    var negated = innerStart < end && pattern[innerStart] == '^';
    var contentStart = negated ? innerStart + 1 : innerStart;
    var content = pattern[contentStart..end];
    yield return (start, end - start + 1, negated ? "[" + content + "]" : "[^" + content + "]", "regex:character-class-negation");
    yield return (start, end - start + 1, "[\\w\\W]", "regex:character-class-any-character");

    var children = new List<(int Start, int Length)>();
    for (var index = contentStart; index < end;)
    {
      var length = pattern[index] == '\\' && index + 1 < end ? 2 : 1;
      if (index + 2 < end && pattern[index + 1] == '-' && pattern[index] != '\\')
      {
        length = 3;
      }
      children.Add((index, length));
      index += length;
    }
    foreach (var child in children)
    {
      if (content.Length - child.Length > 0)
      {
        yield return (child.Start, child.Length, string.Empty, "regex:character-class-child-removal");
      }
    }

    for (var index = contentStart; index + 2 < end; index++)
    {
      if (pattern[index + 1] != '-' || !char.IsAscii(pattern[index]) || !char.IsAscii(pattern[index + 2]))
      {
        continue;
      }
      var low = pattern[index];
      var high = pattern[index + 2];
      if (low + 1 < high)
      {
        yield return (index, 3, (char)(low - 1) + "-" + high, "regex:character-class-range");
        yield return (index, 3, (char)(low + 1) + "-" + high, "regex:character-class-range");
        yield return (index, 3, low + "-" + (char)(high - 1), "regex:character-class-range");
        yield return (index, 3, low + "-" + (char)(high + 1), "regex:character-class-range");
      }
    }
  }

  private static bool TryFindCharacterClassEnd(string pattern, int start, out int end)
  {
    for (var index = start + 1; index < pattern.Length; index++)
    {
      if (pattern[index] == ']' && !IsEscaped(pattern, index) && index > start + 1)
      {
        end = index;
        return true;
      }
    }
    end = -1;
    return false;
  }

  private static bool TryParseRangeQuantifier(string pattern, int start, out int end, out int minimum, out int? maximum)
  {
    end = -1;
    minimum = 0;
    maximum = null;
    var close = pattern.IndexOf('}', start + 1);
    if (close < 0 || !int.TryParse(pattern.AsSpan(start + 1, close - start - 1).ToString().Split(',')[0], out minimum))
    {
      return false;
    }
    var body = pattern[(start + 1)..close];
    if (body.Contains(',', StringComparison.Ordinal))
    {
      var bounds = body.Split(',');
      if (bounds.Length != 2 || bounds[1].Length > 0 && !int.TryParse(bounds[1], out _))
      {
        return false;
      }
      if (bounds[1].Length > 0)
      {
        maximum = int.Parse(bounds[1], System.Globalization.CultureInfo.InvariantCulture);
        if (maximum.Value < minimum)
        {
          return false;
        }
      }
    }
    else
    {
      maximum = minimum;
    }
    end = close;
    return true;
  }

  private static bool IsEscaped(string pattern, int index)
  {
    var slashes = 0;
    for (var cursor = index - 1; cursor >= 0 && pattern[cursor] == '\\'; cursor--)
    {
      slashes++;
    }
    return slashes % 2 != 0;
  }

  private static (string Pattern, string Token) Apply(string pattern, int start, int length, string replacement, string token) =>
      (pattern[..start] + replacement + pattern[(start + length)..], token);

  private sealed class RegexEditComparer : IEqualityComparer<(int Start, int Length, string Replacement)>
  {
    public bool Equals((int Start, int Length, string Replacement) x, (int Start, int Length, string Replacement) y) =>
        x.Start == y.Start && x.Length == y.Length && string.Equals(x.Replacement, y.Replacement, StringComparison.Ordinal);
    public int GetHashCode((int Start, int Length, string Replacement) value) => HashCode.Combine(value.Start, value.Length, value.Replacement);
  }
}

internal sealed class RemovalMutator : IMutator
{
  public MutatorMetadata Metadata { get; } = new("statement-removal", 1, MutationCategory.Removal,
      new HashSet<string>(["return", "break", "continue", "goto", "throw", "yield", "invocation statements", "nested blocks"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.Removal]) };

  public IEnumerable<MutationDescription> Generate(SyntaxNode node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (node is BlockSyntax block && block.Parent is not MethodDeclarationSyntax)
    {
      var reason = GetBlockUnsupportedReason(block);
      if (reason is not null)
      {
        yield return new(Metadata, block, "{ }", "removal:block", block.ToString(), reason);
      }
      else if (CanRemoveBlock(block))
      {
        yield return new(Metadata, block, "{ }", "removal:block", block.ToString());
      }
      yield break;
    }

    if (node is MethodDeclarationSyntax { Body.Statements.Count: > 0 } method)
    {
      var symbol = model.GetDeclaredSymbol(method);
      var blockReplacement = symbol is null ? null : GetEmptyMethodBodyReplacement(method, symbol);
      if (blockReplacement is null)
      {
        yield return new(Metadata, method.Body!, "block emptied", "removal:block", method.Body!.ToString(),
            "The method body cannot be safely emptied while preserving a valid C# method body.");
      }
      else
      {
        yield return new(Metadata, method.Body!, blockReplacement, "removal:block", method.Body!.ToString());
      }
      yield break;
    }

    if (node is not StatementSyntax statement || statement is not (ReturnStatementSyntax or BreakStatementSyntax or
        ContinueStatementSyntax or GotoStatementSyntax or ThrowStatementSyntax or YieldStatementSyntax or ExpressionStatementSyntax))
    {
      yield break;
    }

    if (statement is ExpressionStatementSyntax { Expression: not InvocationExpressionSyntax } ||
        statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation } &&
        invocation.DescendantNodesAndSelf().Any(syntax => syntax is AwaitExpressionSyntax))
    {
      yield break;
    }

    if ((statement is ReturnStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax or
        GotoStatementSyntax or ThrowStatementSyntax) &&
        statement.Ancestors().OfType<SwitchSectionSyntax>().Any())
    {
      yield break;
    }

    if (statement.Ancestors().Any(ancestor => ancestor is FinallyClauseSyntax) &&
        statement is ReturnStatementSyntax or ThrowStatementSyntax)
    {
      yield return new(Metadata, statement, "removed", "removal:unsupported-finally", statement.ToString(),
          "Removing this statement would require a return from a finally block.");
      yield break;
    }

    var statementReplacement = statement switch
    {
      ReturnStatementSyntax { Expression: not null } => "return default;",
      ReturnStatementSyntax => ";",
      BreakStatementSyntax or ContinueStatementSyntax or GotoStatementSyntax => ";",
      ThrowStatementSyntax => GetThrowReplacement(statement, model),
      YieldStatementSyntax yield when yield.IsKind(SyntaxKind.YieldReturnStatement) => "yield break;",
      YieldStatementSyntax => ";",
      ExpressionStatementSyntax => ";",
      _ => null
    };
    if (statementReplacement is null)
    {
      yield break;
    }

    yield return new(Metadata, statement, statementReplacement, "removal:" + statement.Kind(), statement.ToString());
  }

  private static bool CanRemoveBlock(BlockSyntax block)
  {
    if (block.Statements.Count == 0 ||
        block.Parent is WhileStatementSyntax { Condition: LiteralExpressionSyntax condition } &&
        condition.IsKind(SyntaxKind.TrueLiteralExpression))
    {
      return false;
    }

    foreach (var ancestor in block.Ancestors())
    {
      switch (ancestor)
      {
        case LocalFunctionStatementSyntax:
          return true;
        case ConstructorDeclarationSyntax { Parent: StructDeclarationSyntax }:
          return !block.Statements.OfType<ExpressionStatementSyntax>()
              .Any(statement => statement.Expression is AssignmentExpressionSyntax);
        case SwitchSectionSyntax:
          return false;
      }
    }

    return true;
  }

  private static string? GetBlockUnsupportedReason(BlockSyntax block) =>
      block.Ancestors().Any(ancestor => ancestor is TryStatementSyntax or CatchClauseSyntax or FinallyClauseSyntax)
          ? "Removing this nested exception-handling block can make a non-void method fall through; this context is conservatively unsupported."
          : null;

  private static string GetThrowReplacement(StatementSyntax statement, SemanticModel model)
  {
    var method = model.GetEnclosingSymbol(statement.SpanStart) as IMethodSymbol;
    var methodSyntax = method?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
    if (methodSyntax is MethodDeclarationSyntax methodDeclaration && methodDeclaration.DescendantNodes().OfType<YieldStatementSyntax>().Any())
    {
      return "yield break;";
    }

    return method?.ReturnType.SpecialType == SpecialType.System_Void ? ";" : "return default;";
  }

  private static string? GetEmptyMethodBodyReplacement(MethodDeclarationSyntax method, IMethodSymbol symbol)
  {
    if (symbol.ReturnsByRef || symbol.ReturnsByRefReadonly)
    {
      return null;
    }
    if (method.Body!.DescendantNodes().OfType<YieldStatementSyntax>().Any())
    {
      return "yield break;";
    }
    var asyncVoidLike = symbol.IsAsync &&
        (symbol.ReturnType.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask");
    if (symbol.ReturnsVoid || asyncVoidLike)
    {
      return ";";
    }
    return "return default;";
  }
}

internal sealed class InitializationMutator : IMutator
{
  public MutatorMetadata Metadata { get; } = new("initialization", 1, MutationCategory.Initialization,
      new HashSet<string>(["array initializers", "stackalloc array initializers", "object initializers", "collection expressions"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.Initialization]) };

  public IEnumerable<MutationDescription> Generate(SyntaxNode node, SemanticModel model,
      Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
  {
    if (node is VariableDeclaratorSyntax { Initializer.Value: InitializerExpressionSyntax initializer } declarator &&
        initializer.IsKind(SyntaxKind.ArrayInitializerExpression) && initializer.Expressions.Count > 0 &&
        declarator.Parent is VariableDeclarationSyntax { Type: ArrayTypeSyntax arrayType })
    {
      var arrayTypeInfo = model.GetTypeInfo(arrayType).Type;
      var elementsAreBound = initializer.Expressions.All(element => model.GetTypeInfo(element).Type is { TypeKind: not TypeKind.Error and not TypeKind.Dynamic });
      var validation = arrayTypeInfo is IArrayTypeSymbol && elementsAreBound
          ? (Supported: true, Reason: (string?)null)
          : validate(initializer, model);
      var empty = $"new {arrayType} {{ }}";
      yield return new(Metadata, declarator, empty, "initializer:empty", declarator.ToString(), validation.Supported ? null : validation.Reason);
      yield break;
    }

    ExpressionSyntax? expression = node switch
    {
      StackAllocArrayCreationExpressionSyntax { Initializer.Expressions.Count: > 0 } creation =>
          creation.WithInitializer(creation.Initializer.WithExpressions(SyntaxFactory.SeparatedList<ExpressionSyntax>())),
      ObjectCreationExpressionSyntax { Initializer.Expressions.Count: > 0 } creation =>
          creation.WithInitializer(creation.Initializer!.WithExpressions(SyntaxFactory.SeparatedList<ExpressionSyntax>())),
      ImplicitObjectCreationExpressionSyntax { Initializer.Expressions.Count: > 0 } creation =>
          creation.WithInitializer(creation.Initializer!.WithExpressions(SyntaxFactory.SeparatedList<ExpressionSyntax>())),
      ArrayCreationExpressionSyntax { Initializer.Expressions.Count: > 0 } creation =>
          creation.WithInitializer(creation.Initializer!.WithExpressions(SyntaxFactory.SeparatedList<ExpressionSyntax>())),
      ImplicitArrayCreationExpressionSyntax { Initializer.Expressions.Count: > 0 } creation =>
          creation.WithInitializer(creation.Initializer!.WithExpressions(SyntaxFactory.SeparatedList<ExpressionSyntax>())),
      CollectionExpressionSyntax { Elements.Count: > 0 } collection =>
          SyntaxFactory.CollectionExpression(SyntaxFactory.SeparatedList<CollectionElementSyntax>()).WithTriviaFrom(collection),
      _ => null
    };
    if (expression is null)
    {
      yield break;
    }

    var result = validate((ExpressionSyntax)node, model);
    if (!result.Supported && result.Reason == "dynamic or unresolved operand type" &&
        model.GetTypeInfo((ExpressionSyntax)node).Type is { TypeKind: not TypeKind.Error and not TypeKind.Dynamic } &&
        !model.GetDiagnostics(node.Span).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
    {
      result = (true, null);
    }
    yield return new(Metadata, node, expression.ToString(), "initializer:empty", node.ToString(), result.Supported ? null : result.Reason)
    {
      ApplicationKind = node is StackAllocArrayCreationExpressionSyntax
          ? MutationApplicationKind.ExpressionReplacement
          : MutationApplicationKind.BuiltIn
    };
  }
}

internal sealed class StringConstantMutator : IMutator<InvocationExpressionSyntax>
{
  public MutatorMetadata Metadata { get; } = new("string-method-constants", 1, MutationCategory.StringMethods,
      new HashSet<string>(["Substring", "Trim", "ElementAt", "ElementAtOrDefault"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.StringMethods]) };

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
  public MutatorMetadata Metadata { get; } = new("string-values", 1, MutationCategory.String,
      new HashSet<string>(["string literals", "interpolated strings", "string.Empty", "null/empty checks"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.String]) };

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

internal sealed class IsPatternMutator : IMutator<IsPatternExpressionSyntax>
{
  public MutatorMetadata Metadata { get; } = new("is-patterns", 1, MutationCategory.Equality,
      new HashSet<string>(["is pattern", "is not pattern"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.Equality]) };

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
  public MutatorMetadata Metadata { get; } = new("binary-patterns", 1, MutationCategory.Logical,
      new HashSet<string>(["and pattern", "or pattern"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.Logical]) };

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

internal sealed class BooleanConditionMutator : IMutator<ExpressionSyntax>
{
  public MutatorMetadata Metadata { get; } = new("boolean-conditions", 1, MutationCategory.Boolean,
      new HashSet<string>(["if/while/do/for conditions"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.Boolean]) };

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
  public MutatorMetadata Metadata { get; } = new("checked-contexts", 1, MutationCategory.Checked,
      new HashSet<string>(["checked expression", "unchecked expression"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.Checked]) };

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
  public MutatorMetadata Metadata { get; } = new("collection-expressions", 1, MutationCategory.CollectionExpression,
      new HashSet<string>(["C# 12 collection expression"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.CollectionExpression]) };

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

  public MutatorMetadata Metadata { get; } = new("unary-update", 1, MutationCategory.Unary,
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
    yield return new(Metadata with { Category = category },
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
    yield return new(Metadata with { Category = MutationCategory.Update }, node, $"{node.Operand}{replacement}",
        "unary:" + replacement, node.ToString(), result.Supported ? null : result.Reason);
  }
}

internal sealed class ConditionalExpressionMutator : IMutator<ConditionalExpressionSyntax>
{
  public MutatorMetadata Metadata { get; } = new("conditional-expressions", 1, MutationCategory.Conditional,
      new HashSet<string>(["conditional operator"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.Conditional]) };

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
  public MutatorMetadata Metadata { get; } = new("null-coalescing", 1, MutationCategory.NullCoalescing,
      new HashSet<string>(["?? expression"], StringComparer.Ordinal))
  { Categories = new HashSet<MutationCategory>([MutationCategory.NullCoalescing]) };

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

internal sealed class BooleanLiteralMutator : IMutator<LiteralExpressionSyntax>
{
  public MutatorMetadata Metadata { get; } = new("boolean-literals", 1, MutationCategory.Boolean,
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
