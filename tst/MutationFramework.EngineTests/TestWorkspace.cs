using MutationFramework.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.EngineTests;

internal static class TestWorkspace
{
  internal static readonly string Root = FindRepositoryRoot();
  internal static string AppProject => Path.Combine(Root, "samples", "SampleApplication", "SampleApplication.csproj");
  internal static string TestProject => Path.Combine(Root, "samples", "SampleApplication.Tests", "SampleApplication.Tests.csproj");

  internal sealed class CustomIncrementMutator(int expectedRightValue = 1) : IMutator<BinaryExpressionSyntax>
  {
    public MutatorMetadata Metadata { get; } = new($"custom-increment-{expectedRightValue}", 1, MutationCategory.Arithmetic,
        new HashSet<string>(["addition of one"], StringComparer.Ordinal));

    public IEnumerable<MutationDescription> Generate(BinaryExpressionSyntax node, SemanticModel semanticModel,
        Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
    {
      if (!node.IsKind(SyntaxKind.AddExpression) || node.Right is not LiteralExpressionSyntax literal ||
          !Equals(literal.Token.Value, expectedRightValue))
      {
        yield break;
      }

      var validity = validate(node, semanticModel);
      yield return new(Metadata, node, $"({node.Left}) - ({node.Right})", "custom:subtract", node.ToString(),
          validity.Supported ? null : validity.Reason)
      {
        ApplicationKind = MutationApplicationKind.ExpressionReplacement
      };
    }
  }

  internal static MutationOptions Options(int workers, string? output = null, MutationExecutionMode mode = MutationExecutionMode.Auto,
      TestHostIsolationPolicy isolationPolicy = TestHostIsolationPolicy.PerTest, string? testFilter = null,
      IReadOnlyDictionary<string, IReadOnlyList<string>>? resourceLocks = null) => new()
      {
        ProjectPath = AppProject,
        TestProjects = [TestProject],
        MaxWorkers = workers,
        OutputDirectory = output ?? NewOutputDirectory(),
        ExecutionMode = mode,
        TestHostIsolationPolicy = isolationPolicy,
        TestFilter = testFilter,
        ResourceLocksByTestId = resourceLocks ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        TimeoutSeconds = 120
      };

  internal static string NewOutputDirectory() => Path.Combine(Path.GetTempPath(), "MutationFramework.EngineTests", Guid.NewGuid().ToString("N"));

  internal static void CopyProjectSources(string sourceRoot, string destinationRoot)
  {
    Directory.CreateDirectory(destinationRoot);
    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                 .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("obj", StringComparison.OrdinalIgnoreCase))))
    {
      var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
      Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
      File.Copy(file, destination, true);
    }
  }

  internal static string FindRepositoryRoot()
  {
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
    {
      if (File.Exists(Path.Combine(current.FullName, "MutationFramework.slnx")))
      {
        return current.FullName;
      }
    }

    throw new DirectoryNotFoundException("Unable to locate MutationFramework.slnx from the test output directory.");
  }
  private static readonly Lazy<PortableExecutableReference[]> PlatformReferenceCache = new(() =>
      ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray());

  internal static PortableExecutableReference[] PlatformReferences => PlatformReferenceCache.Value;
}
