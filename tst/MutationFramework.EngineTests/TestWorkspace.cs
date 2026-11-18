using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using MutationFramework.Sdk;

namespace MutationFramework.EngineTests;

internal static class TestWorkspace
{
  internal static readonly string Root = FindRepositoryRoot();
  internal static string AppProject => Path.Combine(Root, "samples", "SampleApplication", "SampleApplication.csproj");
  internal static string TestProject => Path.Combine(Root, "samples", "SampleApplication.Tests", "SampleApplication.Tests.csproj");

  internal sealed class DualStatementMutator : IMutator<ExpressionStatementSyntax>
  {
    public MutatorMetadata Metadata
    {
      get;
    } = new("dual-statement", 1, MutationCategory.Removal,
        new HashSet<string>(["two statement replacements"], StringComparer.Ordinal));

    public IEnumerable<MutationDescription> Generate(ExpressionStatementSyntax node, SemanticModel semanticModel,
        Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
    {
      if (node.ToString() is not ("Step();" or "await StepAsync();"))
      {
        yield break;
      }
      foreach (var amount in new[] { 10, 100 })
      {
        yield return new(Metadata, node, $"Calls += {amount};", $"dual:{amount}", node.ToString())
        {
          ApplicationKind = MutationApplicationKind.StatementReplacement
        };
      }
    }
  }

  internal sealed class DefaultRemovalAdapter : IMutator
  {
    private readonly MutatorRegistry _registry = MutatorRegistry.CreateDefault(new HashSet<string>(["statement-removal"]));
    public MutatorMetadata Metadata => _registry.Mutators.Single();
    public IEnumerable<MutationDescription> Generate(SyntaxNode node, SemanticModel semanticModel,
        Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate) => _registry.Generate(node, semanticModel, validate);
  }

  internal sealed class LinkedRecoveryMutator : IMutator<BinaryExpressionSyntax>
  {
    public MutatorMetadata Metadata
    {
      get;
    } = new("linked-recovery", 1, MutationCategory.Arithmetic,
        new HashSet<string>(["linked owner recovery"]));
    public IEnumerable<MutationDescription> Generate(BinaryExpressionSyntax node, SemanticModel semanticModel,
        Func<ExpressionSyntax, SemanticModel, (bool Supported, string? Reason)> validate)
    {
      var owner = semanticModel.GetEnclosingSymbol(node.SpanStart)?.ContainingNamespace.ToString();
      if (owner is not ("ProductLinked" or "ReferenceLinked"))
      {
        yield break;
      }
      yield return new(Metadata, node, owner == "ProductLinked" ? "MissingMutationMember()" : "value - 1", "linked:replacement", node.ToString())
      {
        ApplicationKind = MutationApplicationKind.ExpressionReplacement
      };
    }
  }

  internal static string WriteNUnitProject(string directory, string productionReference, string source)
  {
    Directory.CreateDirectory(directory);
    var project = Path.Combine(directory, "Fixture.Tests.csproj");
    File.WriteAllText(project, $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsTestProject>true</IsTestProject></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.12.0" />
            <PackageReference Include="NUnit" Version="5.0.0" />
            <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
            <ProjectReference Include="{{System.Security.SecurityElement.Escape(Path.GetRelativePath(directory, productionReference))}}" />
          </ItemGroup>
        </Project>
        """);
    File.WriteAllText(Path.Combine(directory, "Tests.cs"), source);
    return project;
  }

  internal sealed class CustomIncrementMutator(int expectedRightValue = 1) : IMutator<BinaryExpressionSyntax>
  {
    public MutatorMetadata Metadata
    {
      get;
    } = new($"custom-increment-{expectedRightValue}", 1, MutationCategory.Arithmetic,
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
