using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

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
