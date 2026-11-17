using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

internal static class MutationSchemaBuilder
{
  internal static void RewriteSources(Dictionary<string, string> files, List<Candidate> candidates, string projectRoot, string workspaceRoot,
      IReadOnlyDictionary<string, CSharpParseOptions>? parseOptionsByFile = null)
  {
    foreach (var (original, target) in files)
    {
      var fullTarget = Path.GetFullPath(target);
      if (!PathSafety.IsWithinDirectory(fullTarget, workspaceRoot))
      {
        throw new InvalidOperationException($"Refusing to rewrite source '{original}' because its mapped path escapes the isolated workspace: '{fullTarget}'.");
      }

      if (!File.Exists(target))
      {
        continue;
      }

      var text = File.ReadAllText(target);
      var parseOptions = parseOptionsByFile?.GetValueOrDefault(original) ?? new CSharpParseOptions(LanguageVersion.Preview);
      var tree = CSharpSyntaxTree.ParseText(text, path: target, options: parseOptions);
      var local = candidates.Where(c => string.Equals(c.File, Path.GetRelativePath(projectRoot, original).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToArray();
      var root = tree.GetRoot();
      var statementProbeSites = local.Where(candidate => root.DescendantNodes().OfType<ReturnStatementSyntax>()
          .Any(statement => statement.Expression?.SpanStart == candidate.Start && statement.Expression.Span.Length == candidate.Length))
          .Select(candidate => candidate.SiteRuntimeId).ToHashSet(StringComparer.Ordinal);
      var rewritten = new SchemaRewriter(local, statementProbeSites).Visit(root)!;
      File.WriteAllText(target, rewritten.ToFullString());
    }
  }

  private sealed class SchemaRewriter(IReadOnlyList<Candidate> candidates, IReadOnlySet<string> statementProbeSites) : CSharpSyntaxRewriter
  {
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
      if (node is null)
      {
        return null;
      }

      var visited = base.Visit(node);
      if (node is ExpressionSyntax originalExpression && visited is ExpressionSyntax visitedExpression)
      {
        var extensionMutations = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.ExpressionReplacement &&
            candidate.Start == node.SpanStart && candidate.Length == node.Span.Length).ToArray();
        if (extensionMutations.Length == 0)
        {
          return visitedExpression;
        }

        var result = visitedExpression;
        foreach (var candidate in extensionMutations)
        {
          var activation = candidate.UseInlineSiteProbe
              ? $"MutationRuntime.IsActiveAndHit({candidate.SiteRuntimeId}, {candidate.MutantRuntimeId})"
              : $"MutationRuntime.IsActive({candidate.MutantRuntimeId})";
          result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression(activation),
              SyntaxFactory.ParenthesizedExpression(SyntaxFactory.ParseExpression(candidate.Replacement)), SyntaxFactory.ParenthesizedExpression(result));
        }

        var site = extensionMutations[0].SiteRuntimeId;
        if (statementProbeSites.Contains(site) || extensionMutations[0].UseInlineSiteProbe)
        {
          return result.WithTriviaFrom(originalExpression);
        }

        return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
            {
              SyntaxFactory.Argument(SyntaxFactory.ParseExpression(site)),
              SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(result))
            }))).WithTriviaFrom(originalExpression);
      }

      if (node is StatementSyntax originalStatement && visited is StatementSyntax visitedStatement)
      {
        var extensionMutations = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.StatementReplacement &&
            candidate.Start == node.SpanStart && candidate.Length == node.Span.Length).ToArray();
        if (extensionMutations.Length == 0)
        {
          return visitedStatement;
        }

        var statements = new List<StatementSyntax> { SyntaxFactory.ParseStatement($"MutationRuntime.Hit({extensionMutations[0].SiteRuntimeId});") };
        foreach (var candidate in extensionMutations)
        {
          statements.Add(SyntaxFactory.IfStatement(SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({candidate.MutantRuntimeId})"),
              SyntaxFactory.Block(SyntaxFactory.ParseStatement(candidate.Replacement)),
              SyntaxFactory.ElseClause(SyntaxFactory.Block(visitedStatement))));
        }
        return SyntaxFactory.Block(statements).WithTriviaFrom(originalStatement);
      }

      return visited;
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
      var visited = (BlockSyntax)base.VisitBlock(node)!;
      var blockMutations = candidates.Where(candidate => candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Operator == nameof(MutationCategory.Removal) && candidate.ReplacementToken == "removal:block").ToArray();
      if (blockMutations.Length == 0)
      {
        return visited;
      }

      var statements = new List<StatementSyntax>
      {
        SyntaxFactory.ParseStatement($"MutationRuntime.Hit({blockMutations[0].SiteRuntimeId});")
      };
      foreach (var candidate in blockMutations)
      {
        var mutation = SyntaxFactory.ParseStatement(candidate.Replacement);
        statements.Add(SyntaxFactory.IfStatement(SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({candidate.MutantRuntimeId})"),
            SyntaxFactory.Block(mutation), SyntaxFactory.ElseClause(visited)));
      }
      return SyntaxFactory.Block(statements).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
      var visited = (ReturnStatementSyntax)base.VisitReturnStatement(node)!;
      StatementSyntax rewritten = visited;
      if (node.Expression is not null)
      {
        var entrySites = candidates.Where(candidate => candidate.Start == node.Expression.SpanStart &&
                candidate.Length == node.Expression.Span.Length && statementProbeSites.Contains(candidate.SiteRuntimeId))
            .Select(candidate => candidate.SiteRuntimeId).Distinct(StringComparer.Ordinal).ToArray();
        if (entrySites.Length > 0)
        {
          var hits = entrySites.Select(site => (StatementSyntax)SyntaxFactory.ParseStatement($"MutationRuntime.Hit({site});"));
          rewritten = SyntaxFactory.Block(hits.Append(visited)).WithTriviaFrom(node);
        }
      }

      var removal = RewriteRemovedStatement(node, rewritten);
      if (removal is not null)
      {
        return removal;
      }
      return rewritten;
    }

    public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitBreakStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitContinueStatement(ContinueStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitContinueStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitGotoStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitThrowStatement(ThrowStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitThrowStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitYieldStatement(YieldStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitYieldStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
      var visited = (StatementSyntax)base.VisitExpressionStatement(node)!;
      return RewriteRemovedStatement(node, visited) ?? visited;
    }

    private BlockSyntax? RewriteRemovedStatement(StatementSyntax original, StatementSyntax visited)
    {
      var candidatesForStatement = candidates.Where(candidate => candidate.Start == original.SpanStart &&
          candidate.Length == original.Span.Length && candidate.ApplicationKind == MutationApplicationKind.BuiltIn &&
          candidate.Operator == nameof(MutationCategory.Removal)).ToArray();
      if (candidatesForStatement.Length == 0)
      {
        return null;
      }

      var body = new List<StatementSyntax> { SyntaxFactory.ParseStatement($"MutationRuntime.Hit({candidatesForStatement[0].SiteRuntimeId});") };
      foreach (var candidate in candidatesForStatement)
      {
        var mutated = SyntaxFactory.ParseStatement(candidate.Replacement);
        body.Add(SyntaxFactory.IfStatement(SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({candidate.MutantRuntimeId})"),
            SyntaxFactory.Block(mutated), SyntaxFactory.ElseClause(SyntaxFactory.Block(visited))));
      }
      return SyntaxFactory.Block(body).WithTriviaFrom(original);
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
      var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
      var ops = candidates.Where(c => c.ApplicationKind == MutationApplicationKind.BuiltIn && c.Start == node.SpanStart && c.Length == node.Span.Length && c.Operator != "Boolean").ToArray();
      if (ops.Any(candidate => candidate.Operator == MutationCategory.NullCoalescing.ToString()))
      {
        return RewriteExpression(node, visited, ops.Where(candidate => candidate.Operator == MutationCategory.NullCoalescing.ToString()).ToArray());
      }
      ExpressionSyntax result = visited;
      foreach (var c in ops)
      {
        var mut = c.ReplacementToken == "~^"
            ? SyntaxFactory.PrefixUnaryExpression(SyntaxKind.BitwiseNotExpression,
                SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(SyntaxKind.ExclusiveOrExpression, visited.Left, visited.Right)))
            : SyntaxFactory.ParseExpression($"({visited.Left}) {c.ReplacementToken} ({visited.Right})");
        var activation = c.UseInlineSiteProbe
            ? $"MutationRuntime.IsActiveAndHit({c.SiteRuntimeId}, {c.MutantRuntimeId})"
            : $"MutationRuntime.IsActive({c.MutantRuntimeId})";
        result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression(activation), SyntaxFactory.ParenthesizedExpression(mut), SyntaxFactory.ParenthesizedExpression(result));
      }
      // Record entry before evaluating the expression, including when it throws.
      if (ops.Length > 0 && !statementProbeSites.Contains(ops[0].SiteRuntimeId) && !ops[0].UseInlineSiteProbe)
      {
        result = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"), SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(SyntaxFactory.ParseExpression(ops[0].SiteRuntimeId)), SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(result)) })));
      }

      return result.WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
      var visited = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          (candidate.Operator is nameof(MutationCategory.Unary) or nameof(MutationCategory.Update) ||
           candidate.Operator == nameof(MutationCategory.Boolean) && candidate.ReplacementToken.StartsWith("unary:", StringComparison.Ordinal))).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitCheckedExpression(CheckedExpressionSyntax node)
    {
      var visited = (CheckedExpressionSyntax)base.VisitCheckedExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Checked)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitCollectionExpression(CollectionExpressionSyntax node)
    {
      var visited = (CollectionExpressionSyntax)base.VisitCollectionExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator is nameof(MutationCategory.CollectionExpression) or nameof(MutationCategory.Initialization)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
      var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator is nameof(MutationCategory.Initialization) or nameof(MutationCategory.Regex)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
      var visited = (ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node)!;
      return RewriteInitialization(node, visited);
    }

    public override SyntaxNode? VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
    {
      var visited = (ArrayCreationExpressionSyntax)base.VisitArrayCreationExpression(node)!;
      return RewriteInitialization(node, visited);
    }

    public override SyntaxNode? VisitImplicitArrayCreationExpression(ImplicitArrayCreationExpressionSyntax node)
    {
      var visited = (ImplicitArrayCreationExpressionSyntax)base.VisitImplicitArrayCreationExpression(node)!;
      return RewriteInitialization(node, visited);
    }

    public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
    {
      var visited = (VariableDeclaratorSyntax)base.VisitVariableDeclarator(node)!;
      var candidate = candidates.FirstOrDefault(mutation => mutation.ApplicationKind == MutationApplicationKind.BuiltIn && mutation.Start == node.SpanStart && mutation.Length == node.Span.Length &&
          mutation.Operator == nameof(MutationCategory.Initialization));
      if (candidate is null || node.Initializer?.Value is not InitializerExpressionSyntax initializer ||
          node.Parent is not VariableDeclarationSyntax { Type: ArrayTypeSyntax arrayType })
      {
        return visited;
      }

      var originalArray = $"new {arrayType} {initializer}";
      var value = SyntaxFactory.ParseExpression(
          $"MutationRuntime.Probe({candidate.SiteRuntimeId}, () => MutationRuntime.IsActive({candidate.MutantRuntimeId}) ? ({candidate.Replacement}) : ({originalArray}))");
      return visited.WithInitializer(visited.Initializer!.WithValue(value.WithTriviaFrom(visited.Initializer.Value)));
    }

    private ExpressionSyntax RewriteInitialization(ExpressionSyntax original, ExpressionSyntax visited)
    {
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == original.SpanStart && candidate.Length == original.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Initialization)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(original, visited, ops);
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
      var visited = (PostfixUnaryExpressionSyntax)base.VisitPostfixUnaryExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Update)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
      var visited = (ConditionalExpressionSyntax)base.VisitConditionalExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Conditional)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
    {
      var visited = (IsPatternExpressionSyntax)base.VisitIsPatternExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator is nameof(MutationCategory.Equality) or nameof(MutationCategory.Logical)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
      var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.String)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
      var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator is nameof(MutationCategory.String) or nameof(MutationCategory.StringMethods) or nameof(MutationCategory.Linq) or nameof(MutationCategory.Math)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
    {
      var visited = (InterpolatedStringExpressionSyntax)base.VisitInterpolatedStringExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
          candidate.Operator == nameof(MutationCategory.String)).ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(node, visited, ops);
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
      var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
      var condition = RewriteCondition(node.Condition, visited.Condition);
      return condition == visited.Condition ? visited : visited.WithCondition(condition);
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
      var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
      var condition = RewriteCondition(node.Condition, visited.Condition);
      return condition == visited.Condition ? visited : visited.WithCondition(condition);
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
      var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
      var condition = RewriteCondition(node.Condition, visited.Condition);
      return condition == visited.Condition ? visited : visited.WithCondition(condition);
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
      var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
      if (node.Condition is null || visited.Condition is null)
      {
        return visited;
      }
      var condition = RewriteCondition(node.Condition, visited.Condition);
      return condition == visited.Condition ? visited : visited.WithCondition(condition);
    }

    private ExpressionSyntax RewriteCondition(ExpressionSyntax original, ExpressionSyntax visited)
    {
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == original.SpanStart && candidate.Length == original.Span.Length &&
          candidate.Operator == nameof(MutationCategory.Boolean) && candidate.ReplacementToken == "boolean:negate-condition").ToArray();
      return ops.Length == 0 ? visited : RewriteExpression(original, visited, ops);
    }

    private ExpressionSyntax RewriteExpression(SyntaxNode original, ExpressionSyntax visited, Candidate[] ops)
    {
      var result = visited;
      foreach (var candidate in ops)
      {
        var mutation = SyntaxFactory.ParseExpression(candidate.Replacement);
        var activation = candidate.UseInlineSiteProbe
            ? $"MutationRuntime.IsActiveAndHit({candidate.SiteRuntimeId}, {candidate.MutantRuntimeId})"
            : $"MutationRuntime.IsActive({candidate.MutantRuntimeId})";
        result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression(activation),
            SyntaxFactory.ParenthesizedExpression(mutation), SyntaxFactory.ParenthesizedExpression(result));
      }
      if (ops.Length != 0 && !statementProbeSites.Contains(ops[0].SiteRuntimeId) && !ops[0].UseInlineSiteProbe)
      {
        var probeMethod = string.IsNullOrWhiteSpace(ops[0].ResultType) ? "MutationRuntime.Probe" : $"MutationRuntime.Probe<{ops[0].ResultType}>";
        result = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression(probeMethod),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
                SyntaxFactory.Argument(SyntaxFactory.ParseExpression(ops[0].SiteRuntimeId)),
                SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(result)) })));
      }
      return result.WithTriviaFrom(original);
    }
    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
      var visited = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
      var ops = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length && candidate.Operator == MutationCategory.Assignment.ToString()).ToArray();
      ExpressionSyntax result = visited;
      foreach (var candidate in ops)
      {
        var mutation = SyntaxFactory.ParseExpression($"{visited.Left} {candidate.ReplacementToken} ({visited.Right})");
        var activation = candidate.UseInlineSiteProbe
            ? $"MutationRuntime.IsActiveAndHit({candidate.SiteRuntimeId}, {candidate.MutantRuntimeId})"
            : $"MutationRuntime.IsActive({candidate.MutantRuntimeId})";
        result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression(activation),
            SyntaxFactory.ParenthesizedExpression(mutation), SyntaxFactory.ParenthesizedExpression(result));
      }
      if (ops.Length > 0 && !statementProbeSites.Contains(ops[0].SiteRuntimeId) && !ops[0].UseInlineSiteProbe)
      {
        result = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
                        SyntaxFactory.Argument(SyntaxFactory.ParseExpression(ops[0].SiteRuntimeId)),
                        SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(result)) })));
      }

      return result.WithTriviaFrom(node);
    }
    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
      var c = candidates.FirstOrDefault(x => x.ApplicationKind == MutationApplicationKind.BuiltIn && x.Operator == "Boolean" && x.Start == node.SpanStart); if (c == null)
      {
        var stringOps = candidates.Where(candidate => candidate.ApplicationKind == MutationApplicationKind.BuiltIn && candidate.Start == node.SpanStart && candidate.Length == node.Span.Length &&
            candidate.Operator == nameof(MutationCategory.String)).ToArray();
        var visited = (LiteralExpressionSyntax)base.VisitLiteralExpression(node)!;
        return stringOps.Length == 0 ? visited : RewriteExpression(node, visited, stringOps);
      }

      var activation = c.UseInlineSiteProbe
          ? $"MutationRuntime.IsActiveAndHit({c.SiteRuntimeId}, {c.MutantRuntimeId})"
          : $"MutationRuntime.IsActive({c.MutantRuntimeId})";
      var expr = (ExpressionSyntax)SyntaxFactory.ParseExpression($"{activation} ? {c.Replacement} : {node}").WithTriviaFrom(node);
      if (statementProbeSites.Contains(c.SiteRuntimeId) || c.UseInlineSiteProbe)
      {
        return expr.WithTriviaFrom(node);
      }

      return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"), SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(SyntaxFactory.ParseExpression(c.SiteRuntimeId)), SyntaxFactory.Argument(SyntaxFactory.ParenthesizedLambdaExpression(expr)) }))).WithTriviaFrom(node);
    }
  }

  internal static List<(Candidate Candidate, string Diagnostic)> FindInvalidCandidates(string buildOutput, IReadOnlyList<Candidate> candidates, string appCopy)
  {
    const string diagnosticPattern = @"^(?<path>.+?)\((?<line>\d+),(?<column>\d+)\): error (?<code>CS\d+): (?<message>.*)$";
    var failures = new Dictionary<string, (Candidate Candidate, string Diagnostic)>(StringComparer.Ordinal);
    foreach (Match match in Regex.Matches(buildOutput, diagnosticPattern, RegexOptions.Multiline))
    {
      var file = Path.GetFullPath(match.Groups["path"].Value.Trim());
      var line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
      var diagnostic = $"{match.Groups["code"].Value}: {match.Groups["message"].Value.Trim()}";
      foreach (var candidate in candidates.Where(candidate => candidate.Line == line &&
                   string.Equals(Path.GetFullPath(Path.Combine(appCopy, candidate.File)), file, StringComparison.OrdinalIgnoreCase)))
      {
        failures.TryAdd(candidate.Id, (candidate, diagnostic));
      }
    }
    return failures.Values.OrderBy(failure => failure.Candidate.File, StringComparer.Ordinal)
        .ThenBy(failure => failure.Candidate.Line).ThenBy(failure => failure.Candidate.Column)
        .ThenBy(failure => failure.Candidate.Id, StringComparer.Ordinal).ToList();
  }
}
