using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

internal sealed record CandidateDiscoveryArtifact(Candidate[] Candidates, MutationResult[] UnsupportedMutations, string[] Diagnostics);

internal sealed record Candidate(string Id, string SiteId, string Operator, string File, int Start, int Length, int Line, int Column, string Original, string Replacement, string ReplacementToken, string SiteRuntimeId, string MutantRuntimeId, string? ResultType = null, MutationApplicationKind ApplicationKind = MutationApplicationKind.BuiltIn)
{
  public bool UseInlineSiteProbe
  {
    get; init;
  }
  public string? OwnerProject
  {
    get; init;
  }
}

internal static class MutationDiscovery
{
  internal static async Task<List<Candidate>> DiscoverCandidatesCached(string[] files, string project, MutationOptions options, List<string> diagnostics,
      CancellationToken cancellationToken, List<MutationResult> unsupportedMutations, ProjectEvaluation evaluation, ArtifactCache cache,
      IReadOnlyList<ProjectSemanticCompilation>? projectCompilations = null, RunMetrics? metrics = null)
  {
    var semanticCompilations = projectCompilations ?? await ProjectSemanticCompilations.LoadAsync(project, evaluation, cache, diagnostics, cancellationToken, metrics);
    var projectIdentity = Path.GetFullPath(project) + "|" + evaluation.TargetFramework;
    var configurationInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string? unresolvedImport = null;
    foreach (var path in evaluation.ProjectPaths)
    {
      configurationInputs.UnionWith(BuildInputs.ProjectConfigurationInputs(path, out var unresolved));
      unresolvedImport ??= unresolved;
    }
    var cacheInputs = files.Concat(configurationInputs).Distinct(StringComparer.OrdinalIgnoreCase);
    var values = new List<string>
    {
      "mutation-discovery-v2", string.Join(";", semanticCompilations.Select(compilation => compilation.Fingerprint)), projectIdentity, evaluation.AssemblyName, evaluation.ParseOptions.LanguageVersion.ToString(),
      string.Join(";", evaluation.ParseOptions.PreprocessorSymbolNames.Order(StringComparer.Ordinal)),
      string.Join(";", evaluation.AssemblyNamesByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)),
      string.Join(";", evaluation.ParseOptionsByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
          .Select(pair => pair.Key + "=" + pair.Value.LanguageVersion + ":" + string.Join(",", pair.Value.PreprocessorSymbolNames.Order(StringComparer.Ordinal)))),
      CacheIdentity.MutatorFingerprint(options),
      options.Include ?? string.Empty, options.Exclude ?? string.Empty, CacheIdentity.EnvironmentFingerprint(),
      CacheIdentity.EngineVersion()
    };
    var contentKey = ArtifactCache.ContentKey(cacheInputs, values);
    if (unresolvedImport is not null)
    {
      diagnostics.Add($"CacheBypass: mutation discovery found an MSBuild import with an unresolved property path ({unresolvedImport}).");
    }
    else
    {
      var cacheRead = await cache.TryReadAsync<CandidateDiscoveryArtifact>("mutation-discovery", projectIdentity,
          contentKey, cancellationToken);
      if (cacheRead.Found)
      {
        diagnostics.Add(cacheRead.Diagnostic);
        var artifact = cacheRead.Value!;
        diagnostics.AddRange(artifact!.Diagnostics);
        unsupportedMutations.AddRange(artifact.UnsupportedMutations);
        return artifact.Candidates.ToList();
      }
      diagnostics.Add(cacheRead.Diagnostic);
    }
    var localDiagnostics = new List<string>();
    var localUnsupported = new List<MutationResult>();
    var productionOwners = evaluation.SourceOwnership.Select(owner => owner.Project).Append(Path.GetFullPath(project)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var candidates = DiscoverCandidates(files, project, options, localDiagnostics, cancellationToken, localUnsupported,
        MutationCompilationContext.From(evaluation) with
        {
          ProjectCompilations = semanticCompilations.Where(owner => productionOwners.Contains(owner.Project)).ToArray()
        });
    diagnostics.AddRange(localDiagnostics);
    unsupportedMutations.AddRange(localUnsupported);
    if (unresolvedImport is null)
    {
      await cache.WriteAsync("mutation-discovery", projectIdentity, contentKey,
          new CandidateDiscoveryArtifact(candidates.ToArray(), localUnsupported.ToArray(), localDiagnostics.ToArray()), cancellationToken);
    }
    return candidates;
  }

  internal static List<Candidate> DiscoverCandidates(string[] files, string project, MutationOptions opt, List<string> diagnostics,
      CancellationToken ct, List<MutationResult>? unsupportedMutations = null, MutationCompilationContext? context = null)
  {
    var list = new List<Candidate>();
    var root = Path.GetDirectoryName(Path.GetFullPath(project))!;
    var parseOptions = context?.ParseOptions ?? new CSharpParseOptions(LanguageVersion.Preview);
    var targetFramework = context?.TargetFramework ?? opt.TargetFramework ?? "net11.0";
    var assemblyIdentity = context?.AssemblyIdentity ?? Path.GetFileNameWithoutExtension(project);
    var inputs = new List<(string File, SyntaxTree Tree, CSharpCompilation Compilation, string Assembly, string Framework, string? Owner)>();
    if (context?.ProjectCompilations.Count > 0)
    {
      var requested = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach (var owner in context.ProjectCompilations)
      {
        foreach (var tree in owner.Compilation.SyntaxTrees.Where(tree => requested.Contains(tree.FilePath)))
        {
          inputs.Add((tree.FilePath, tree, owner.Compilation, owner.Compilation.AssemblyName!, owner.TargetFramework, owner.Project));
        }
      }
    }
    else
    {
      // The synchronous syntax-fixture API supplies its own in-memory compilation context.
      // Production discovery always enters through the evaluated compiler invocations above.
      var trees = files.Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file,
          options: context?.ParseOptionsByFile?.GetValueOrDefault(file) ?? parseOptions)).ToArray();
      var references = (((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator,
          StringSplitOptions.RemoveEmptyEntries) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path));
      var fixtureCompilation = CSharpCompilation.Create(assemblyIdentity, trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      inputs.AddRange(trees.Select(tree => (tree.FilePath, (SyntaxTree)tree, fixtureCompilation,
          context?.AssemblyNamesByFile?.GetValueOrDefault(tree.FilePath) ?? assemblyIdentity, targetFramework, (string?)null)));
    }
    var registry = opt.Registry ?? MutatorRegistry.CreateDefault(opt.Mutators);
    foreach (var (file, tree, compilation, fileAssemblyIdentity, fileFramework, ownerProject) in inputs)
    {
      ct.ThrowIfCancellationRequested();
      var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
      if (IsGeneratedSource(file, tree.GetText(ct).ToString()))
      {
        continue;
      }

      if (opt.Include != null && !rel.Contains(opt.Include, StringComparison.OrdinalIgnoreCase) || opt.Exclude != null && rel.Contains(opt.Exclude, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
      foreach (var node in tree.GetRoot().DescendantNodes())
      {
        foreach (var description in registry.Generate(node, model, IsSupportedMutationSite))
        {
          var target = description.Node;
          var identityPrefix = fileAssemblyIdentity + "|" + fileFramework + "|" + rel + "|" + target.SpanStart + "|";
          // Nested left-associative expressions share a start position and operator.
          // Their lengths distinguish separate mutations (for example a || b || c).
          var id = CacheIdentity.Hash(identityPrefix + target.Span.Length + "|" + description.ReplacementToken);
          var site = CacheIdentity.Hash(identityPrefix + target.Span.Length);
          var line = tree.GetLineSpan(target.Span).StartLinePosition;
          var reason = description.UnsupportedReason ?? GetSchemaCompatibilityFailure(description);
          if (reason is not null)
          {
            diagnostics.Add($"Unsupported {description.Mutator.Category} mutation at {rel}:{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}: {reason}");
            unsupportedMutations?.Add(new MutationResult(id, site, description.Mutator.Category.ToString(), rel,
                line.Line + 1, line.Character + 1, description.Original, description.Replacement,
                MutationStatus.Unsupported, [], [], Diagnostic: reason));
            continue;
          }
          list.Add(new Candidate(id, site, description.Mutator.Category.ToString(), rel, target.SpanStart, target.Span.Length,
              line.Line + 1, line.Character + 1, description.Original, description.Replacement,
              description.ReplacementToken, "0", "0", ResultTypeName(target, model), description.ApplicationKind)
          {
            OwnerProject = ownerProject,
            UseInlineSiteProbe = target is ExpressionSyntax candidateExpression &&
                (HasExplicitOverflowContext(candidateExpression) || IsExpressionTreeContext(candidateExpression, model) ||
                    model.GetTypeInfo(candidateExpression).Type?.IsRefLikeType == true ||
                    model.GetTypeInfo(candidateExpression).ConvertedType?.IsRefLikeType == true ||
                    model.GetTypeInfo(candidateExpression).Type?.TypeKind == TypeKind.Pointer ||
                    model.GetTypeInfo(candidateExpression).ConvertedType?.TypeKind == TypeKind.Pointer)
          });
        }
      }
    }
    var unique = list.GroupBy(x => (x.OwnerProject, x.File, x.Start, x.Length, x.ReplacementToken)).Select(x => x.First()).OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    ReindexCandidates(unique);
    return unique;
  }

  private static string? ResultTypeName(SyntaxNode node, SemanticModel model)
  {
    if (node is not ExpressionSyntax expression)
    {
      return null;
    }
    var type = model.GetTypeInfo(expression).ConvertedType ?? model.GetTypeInfo(expression).Type;
    static bool ContainsAnonymousType(ITypeSymbol? symbol) => symbol switch
    {
      INamedTypeSymbol named => named.IsAnonymousType || named.TypeArguments.Any(ContainsAnonymousType),
      IArrayTypeSymbol array => ContainsAnonymousType(array.ElementType),
      _ => false
    };
    return ContainsAnonymousType(type) ? null : type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
  }

  internal static string? GetSchemaCompatibilityFailure(MutationDescription description) => description.ApplicationKind switch
  {
    MutationApplicationKind.BuiltIn => null,
    MutationApplicationKind.ExpressionReplacement when description.Node is not ExpressionSyntax =>
        "Expression replacement schema requires an expression syntax node.",
    MutationApplicationKind.StatementReplacement when description.Node is not StatementSyntax =>
        "Statement replacement schema requires a statement syntax node.",
    MutationApplicationKind.StatementReplacement when description.Node is LocalDeclarationStatementSyntax or LocalFunctionStatementSyntax or LabeledStatementSyntax =>
        "Statement dispatch cannot preserve the declaration scope, label visibility, or disposal lifetime of this statement.",
    MutationApplicationKind.StatementReplacement when description.Node.DescendantNodes(node => node is not BlockSyntax)
        .Any(node => node is SingleVariableDesignationSyntax) =>
        "Statement dispatch cannot preserve the enclosing scope of an out-variable or pattern declaration.",
    MutationApplicationKind.ExpressionReplacement or MutationApplicationKind.StatementReplacement => null,
    _ => $"Unknown mutation application schema '{description.ApplicationKind}'."
  };

  internal static bool IsGeneratedSource(string path, string source)
  {
    var fileName = Path.GetFileName(path);
    if (fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
    {
      return true;
    }

    var headerLength = Math.Min(source.Length, 2048);
    return source.AsSpan(0, headerLength).Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
  }

  internal static (bool Supported, string? Reason) IsSupportedMutationSite(ExpressionSyntax expression, SemanticModel model)
  {
    // Provider queries inspect expression-tree shape. Even an inactive activation
    // or coverage call changes that shape and can make an otherwise valid SQL query
    // untranslatable. Query syntax has implicit lambdas, so it needs its own guard.
    if (expression.Ancestors().OfType<QueryExpressionSyntax>().Any() || IsExpressionTreeContext(expression, model))
    {
      return (false, "provider expression-tree queries cannot contain runtime mutation probes");
    }
    var operation = model.GetOperation(expression);
    var contextualArrayInitializer = expression is (ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax) &&
        model.GetTypeInfo(expression).Type is IArrayTypeSymbol ||
        expression is InitializerExpressionSyntax arrayInitializer && arrayInitializer.IsKind(SyntaxKind.ArrayInitializerExpression) &&
        arrayInitializer.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type: ArrayTypeSyntax arrayType } } } &&
        model.GetTypeInfo(arrayType).Type is IArrayTypeSymbol;
    if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression) &&
        (model.GetTypeInfo(binary.Left).Type?.SpecialType == SpecialType.System_String || model.GetTypeInfo(binary.Right).Type?.SpecialType == SpecialType.System_String))
    {
      return (false, "string concatenation is excluded");
    }
    foreach (var syntax in expression.DescendantNodesAndSelf())
    {
      if (syntax is AwaitExpressionSyntax)
      {
        return (false, "await expressions cannot be wrapped without changing async semantics");
      }
      if (syntax is not ExpressionSyntax current)
      {
        continue;
      }

      var type = model.GetTypeInfo(current).Type;
      var isArrayInitializer = syntax is InitializerExpressionSyntax initializer && initializer.IsKind(SyntaxKind.ArrayInitializerExpression);
      if (type?.TypeKind == TypeKind.Error && !(contextualArrayInitializer && isArrayInitializer))
      {
        return (false, "dynamic or unresolved operand type");
      }
      var stackAllocation = expression is StackAllocArrayCreationExpressionSyntax;
      if (type?.IsRefLikeType == true && !stackAllocation ||
          type?.TypeKind == TypeKind.Pointer && !stackAllocation || type is ITypeParameterSymbol)
      {
        return (false, $"ref-like, pointer, or generic operand type '{type?.ToDisplayString()}' in '{current}'");
      }
      if (current is IdentifierNameSyntax identifier && model.GetSymbolInfo(identifier).Symbol is ILocalSymbol local && (local.RefKind != RefKind.None || local.Type.IsRefLikeType))
      {
        return (false, "ref or ref-like local capture");
      }
    }
    if (expression.Ancestors().Any(x => x is AttributeArgumentSyntax or CaseSwitchLabelSyntax or ConstantPatternSyntax) ||
        expression.Ancestors().OfType<ParameterSyntax>().Any(p => p.Default?.Value == expression) ||
        expression.Ancestors().OfType<VariableDeclaratorSyntax>().Any(v => v.Initializer?.Value == expression && v.Parent?.Parent is FieldDeclarationSyntax field && field.Modifiers.Any(SyntaxKind.ConstKeyword)) ||
        expression.Ancestors().OfType<LocalDeclarationStatementSyntax>().Any(d => d.Modifiers.Any(SyntaxKind.ConstKeyword) && d.Declaration.Variables.Any(v => v.Initializer?.Value == expression)))
    {
      return (false, "constant-expression context");
    }
    if (operation == null && !contextualArrayInitializer)
    {
      return (false, "Roslyn could not bind the expression");
    }
    return (true, null);
  }

  internal static bool IsExpressionTreeContext(ExpressionSyntax expression, SemanticModel model) =>
      expression.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any(lambda =>
          model.GetTypeInfo(lambda).ConvertedType is INamedTypeSymbol named &&
          named.OriginalDefinition.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>");

  internal static bool HasExplicitOverflowContext(ExpressionSyntax expression) => expression.Ancestors().Any(node =>
      node.IsKind(SyntaxKind.CheckedExpression) || node.IsKind(SyntaxKind.UncheckedExpression) ||
      node.IsKind(SyntaxKind.CheckedStatement) || node.IsKind(SyntaxKind.UncheckedStatement));

  internal static void ReindexCandidates(List<Candidate> candidates)
  {
    var siteIndices = candidates.Select(candidate => candidate.SiteId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index.ToString(), StringComparer.Ordinal);
    var mutationIndices = candidates.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index.ToString(), StringComparer.Ordinal);
    for (var index = 0; index < candidates.Count; index++)
    {
      candidates[index] = candidates[index] with
      {
        SiteRuntimeId = siteIndices[candidates[index].SiteId],
        MutantRuntimeId = mutationIndices[candidates[index].Id]
      };
    }
  }
}
