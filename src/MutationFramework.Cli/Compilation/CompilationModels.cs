using Microsoft.CodeAnalysis.CSharp;

namespace MutationFramework.Sdk;

internal sealed record ProjectEvaluation(string[] CompileFiles, CSharpParseOptions ParseOptions, string? TargetFramework, string AssemblyName,
    string[] ProjectReferences, string[] ProjectPaths, IReadOnlyDictionary<string, string> AssemblyNamesByFile)
{
  public IReadOnlyDictionary<string, string?> TargetFrameworksByProject { get; init; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
  public IReadOnlyDictionary<string, string[]> ReferencesByProject { get; init; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
  public IReadOnlyList<ProjectSourceOwnership> SourceOwnership { get; init; } = [];
  public string[] BuildOnlyProjectReferences { get; init; } = [];
  public string? TargetPath
  {
    get; init;
  }
  public LinkedCompileItem[] LinkedCompileItems { get; init; } = [];
  public string[] BuildInputs { get; init; } = [];
  public bool HasUnsafeGeneratedCompileInputs
  {
    get; init;
  }
  public IReadOnlyDictionary<string, CSharpParseOptions> ParseOptionsByFile
  {
    get; init;
  } =
      new Dictionary<string, CSharpParseOptions>(StringComparer.OrdinalIgnoreCase);
}

internal sealed record ProjectSourceOwnership(string Project, string[] Files, CSharpParseOptions ParseOptions, string? TargetFramework = null);

internal sealed record LinkedCompileItem(string File, string Identity, string DefiningProjectPath, string ProjectPath);

internal sealed record MutationCompilationContext(CSharpParseOptions? ParseOptions = null, string? TargetFramework = null,
    string? AssemblyIdentity = null, IReadOnlyDictionary<string, string>? AssemblyNamesByFile = null,
    IReadOnlyDictionary<string, CSharpParseOptions>? ParseOptionsByFile = null)
{
  public IReadOnlyList<ProjectSemanticCompilation> ProjectCompilations { get; init; } = [];

  internal static MutationCompilationContext From(ProjectEvaluation evaluation) => new(evaluation.ParseOptions,
      evaluation.TargetFramework, evaluation.AssemblyName, evaluation.AssemblyNamesByFile, evaluation.ParseOptionsByFile);
}

internal sealed record MutationProjects(string App, string[] Tests, ProjectEvaluation Production,
    List<ProjectEvaluation> TestEvaluations);
