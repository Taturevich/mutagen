using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

using static MutationFramework.EngineTests.TestWorkspace;

[TestFixture]
public sealed class StatementConformanceTests
{
  [Test]
  [Category("EndToEnd")]
  public async Task AssignmentOperatorMutationsHaveCompleteCompileableGenerationAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenAssignmentParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Assignment.csproj");
      var source = Path.Combine(root, "Assignment.cs");
      var target = Path.Combine(root, "instrumented", "Assignment.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      var operations = new (string Name, string Operator)[]
      {
        ("Add", "+="), ("Subtract", "-="), ("Multiply", "*="), ("Divide", "/="), ("Modulo", "%="),
        ("And", "&="), ("Or", "|="), ("Xor", "^="), ("LeftShift", "<<="), ("RightShift", ">>="), ("UnsignedRightShift", ">>>=")
      };
      File.WriteAllText(source, "public static class Assignment { " +
          string.Join(" ", operations.Select(operation => $"public static int {operation.Name}(int a, int b) {{ a {operation.Operator} b; return a; }}")) +
          " public static int? Coalesce(int? a, int? b) { a ??= b; return a; } }");
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None);
      Assert.That(candidates.Count(candidate => candidate.Operator == nameof(MutationCategory.Assignment)), Is.EqualTo(18));
      using (var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json"))))
      {
        var documented = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Assignment Statements (_assignment_)" &&
                row.GetProperty("status").GetString() == "supported")
            .Select(row => (From: row.GetProperty("from").GetString()!, To: row.GetProperty("to").GetString()!)).ToArray();
        Assert.That(documented, Has.Length.EqualTo(14));
        foreach (var transformation in documented)
        {
          Assert.That(candidates.Any(candidate => candidate.Operator == nameof(MutationCategory.Assignment) &&
              candidate.Original.Contains(transformation.From, StringComparison.Ordinal) &&
              candidate.ReplacementToken == transformation.To), Is.True,
              $"Missing documented assignment transformation: {transformation.From} -> {transformation.To}");
        }
      }
      Assert.That(candidates.Select(candidate => candidate.ReplacementToken), Does.Contain("<<="));
      Assert.That(candidates.Select(candidate => candidate.ReplacementToken), Does.Contain(">>>="));
      Assert.That(candidates.Select(candidate => candidate.ReplacementToken), Does.Contain("="));
      File.Copy(source, target);
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = PlatformReferences;
      var compilation = CSharpCompilation.Create("AssignmentFixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      Assert.That(compilation.Emit(bytes).Success, Is.True);
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("Assignment")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      foreach (var candidate in candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Assignment)))
      {
        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var method = candidate.Original.Contains("??=", StringComparison.Ordinal)
            ? "Coalesce"
            : operations.Single(operation => operation.Operator == candidate.Original.Split(' ')[1]).Name;
        Assert.DoesNotThrow(() => fixture.GetMethod(method)!.Invoke(null, candidate.Original.Contains("??=", StringComparison.Ordinal) ? [3, 1] : [7, 1]), candidate.Replacement);
      }
      var coalescing = candidates.Single(candidate => candidate.Original == "a ??= b");
      active.SetValue(null, int.Parse(coalescing.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Assert.That(fixture.GetMethod("Coalesce")!.Invoke(null, [3, 2]), Is.EqualTo(2));

      var engineAssertions = new List<string>();
      var integerInputs = (from a in Enumerable.Range(-24, 49)
                           from b in new[] { -7, -3, -2, -1, 1, 2, 3, 7, 16 }
                           select (A: a, B: b)).ToArray();
      foreach (var operation in operations)
      {
        foreach (var (a, b) in integerInputs)
        {
          active.SetValue(null, -1);
          var expected = (int)fixture.GetMethod(operation.Name)!.Invoke(null, [a, b])!;
          engineAssertions.Add($"Assert.That(Assignment.{operation.Name}({a}, {b}), Is.EqualTo({expected}));");
        }
      }
      var nullableInputs = (from a in new int?[] { null, -1, 0, 1, 3 }
                            from b in new int?[] { null, -2, 0, 2, 5 }
                            select (A: a, B: b)).ToArray();
      foreach (var (a, b) in nullableInputs)
      {
        active.SetValue(null, -1);
        var expected = fixture.GetMethod("Coalesce")!.Invoke(null, [a, b]);
        engineAssertions.Add($"Assert.That(Assignment.Coalesce({a?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}, {b?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}), Is.EqualTo({expected?.ToString() ?? "null"}));");
      }
      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "Assignment.csproj");
      var testProject = Path.Combine(testsDirectory, "Assignment.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><AssemblyName>Assignment.Product</AssemblyName></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "Assignment.cs"), File.ReadAllText(source));
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\Assignment.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "AssignmentConformanceTests.cs"),
          "using NUnit.Framework; [TestFixture] public sealed class AssignmentConformanceTests { [Test] public void EveryAssignmentMutationIsCheckedAgainstOriginalBehavior() { " +
          string.Join(" ", engineAssertions) + " } }");
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["assignment-expressions"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      var assignmentCandidates = candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Assignment)).ToArray();
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(assignmentCandidates.Length));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "Assignment.cs" && mutant.Line == 1 && mutant.Column > 0), Is.True);
      Assert.That(engineResult.Mutants.All(mutant => assignmentCandidates.Any(candidate =>
          candidate.Original == mutant.Original && candidate.Replacement == mutant.Replacement)), Is.True);
      Assert.That(engineResult.Counters.CoveredMutants, Is.EqualTo(assignmentCandidates.Length));
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task InitializerMutationsCompileActivateAndKillInEngineAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenInitializerParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      var target = Path.Combine(root, "instrumented", "Fixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "using System; using System.Collections.Generic; using System.Collections.ObjectModel; public sealed class SomeClass { public string Foo { get; set; } = string.Empty; } public static class Fixture { public static int[] Array() => new int[] { 1, 2 }; public static int[] VariableArray() { int[] numbers = { 1, 2 }; return numbers; } public static List<int> List() => new List<int> { 1, 2 }; public static Collection<int> LegacyCollection() => new Collection<int> { 1, 2 }; public static Dictionary<int, int> Map() => new Dictionary<int, int> { { 1, 1 } }; public static SomeClass Object() => new SomeClass { Foo = \"Bar\" }; public static int[] Collection() => [1, 2]; public static int StackArray() { Span<int> values = stackalloc int[] { 1, 2 }; return values.Length == 2 ? values[0] + values[1] : 0; } public static int StackArraySized() { Span<int> values = stackalloc int[2]; return values.Length; } }");
      var unsupported = new List<MutationResult>();
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported);
      var initializers = candidates.Where(candidate => candidate.Operator == nameof(MutationCategory.Initialization)).ToArray();
      Assert.That(initializers, Has.Length.EqualTo(8), string.Join(Environment.NewLine, unsupported.Select(mutation => $"{mutation.SourceFile}:{mutation.Line}: {mutation.Original} -> {mutation.Replacement}: {mutation.Diagnostic}")));
      var stackAllocation = initializers.SingleOrDefault(candidate => candidate.Original.Contains("stackalloc", StringComparison.Ordinal));
      Assert.That(stackAllocation, Is.Not.Null, "The pinned Stryker.NET ArrayCreationMutator also clears initialized stackalloc expressions.");
      Assert.That(stackAllocation!.Replacement, Does.Contain("stackalloc int[] { }"));
      Assert.That(stackAllocation.ApplicationKind, Is.EqualTo(MutationApplicationKind.ExpressionReplacement));
      Assert.That(stackAllocation.UseInlineSiteProbe, Is.True, "Ref-like stackalloc expressions must use inline activation, not the generic probe delegate.");
      Assert.That(initializers.Any(candidate => candidate.Original.Contains("stackalloc int[2]", StringComparison.Ordinal)), Is.False,
          "Stack allocations with an explicit size and no initializer are not eligible for initializer removal.");
      using (var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json"))))
      {
        var transformations = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Initialization (_initializer_)" &&
                row.GetProperty("status").GetString() == "supported")
            .Select(row => (From: row.GetProperty("from").GetString()!.TrimEnd(';'), To: row.GetProperty("to").GetString()!.TrimEnd(';'))).ToArray();
        Assert.That(transformations, Has.Length.EqualTo(6));
        var sourceDerived = parity.RootElement.GetProperty("sourceDerivedCases").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Initialization (_initializer_)" &&
                row.GetProperty("status").GetString() == "supported")
            .ToArray();
        Assert.That(sourceDerived, Has.Length.EqualTo(1));
        Assert.That(sourceDerived[0].GetProperty("source").GetString(), Does.EndWith("ArrayCreationMutator.cs"));
        Assert.That(sourceDerived[0].GetProperty("from").GetString(), Is.EqualTo("stackalloc int[] { 1, 2 }"));
        Assert.That(sourceDerived[0].GetProperty("to").GetString(), Is.EqualTo("stackalloc int[] { }"));
        static string NormalizeInitializer(string value) => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
        foreach (var transformation in transformations)
        {
          var equivalentOriginal = transformation.From.StartsWith("int[] numbers =", StringComparison.Ordinal)
              ? transformation.From["int[] ".Length..]
              : transformation.From;
          var equivalentReplacement = transformation.From.StartsWith("int[] numbers =", StringComparison.Ordinal)
              ? "new int[] { }"
              : transformation.To;
          Assert.That(initializers.Any(candidate => NormalizeInitializer(candidate.Original) == NormalizeInitializer(equivalentOriginal) &&
              NormalizeInitializer(candidate.Replacement) == NormalizeInitializer(equivalentReplacement)), Is.True,
              $"Missing documented initializer transformation: {transformation.From} -> {transformation.To}. Candidates: " +
              string.Join(" | ", initializers.Select(candidate => $"{candidate.Original} -> {candidate.Replacement}")));
        }
        Assert.That(initializers.Any(candidate => NormalizeInitializer(candidate.Original) == NormalizeInitializer(sourceDerived[0].GetProperty("from").GetString()!) &&
            NormalizeInitializer(candidate.Replacement) == NormalizeInitializer(sourceDerived[0].GetProperty("to").GetString()!)), Is.True,
            "Missing source-derived Stryker initializer transformation for stackalloc.");
      }
      File.Copy(source, target);
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static bool IsActiveAndHit(int siteId, int id) { Hit(siteId); return IsActive(id); } public static void Hit(int id) { } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = PlatformReferences;
      var compilation = CSharpCompilation.Create("InitializerFixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("Fixture")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      Assert.That(((int[])fixture.GetMethod("Array")!.Invoke(null, null)!).Length, Is.EqualTo(2));
      Assert.That(((int[])fixture.GetMethod("VariableArray")!.Invoke(null, null)!).Length, Is.EqualTo(2));
      Assert.That(((List<int>)fixture.GetMethod("List")!.Invoke(null, null)!).Count, Is.EqualTo(2));
      Assert.That(((System.Collections.ObjectModel.Collection<int>)fixture.GetMethod("LegacyCollection")!.Invoke(null, null)!).Count, Is.EqualTo(2));
      Assert.That(((Dictionary<int, int>)fixture.GetMethod("Map")!.Invoke(null, null)!).Count, Is.EqualTo(1));
      Assert.That((string)fixture.GetMethod("Object")!.Invoke(null, null)!.GetType().GetProperty("Foo")!.GetValue(fixture.GetMethod("Object")!.Invoke(null, null))!, Is.EqualTo("Bar"));
      Assert.That(((int[])fixture.GetMethod("Collection")!.Invoke(null, null)!).Length, Is.EqualTo(2));
      Assert.That(fixture.GetMethod("StackArray")!.Invoke(null, null), Is.EqualTo(3));
      foreach (var candidate in initializers)
      {
        active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
        var result = candidate.Original.StartsWith("numbers =", StringComparison.Ordinal)
            ? fixture.GetMethod("VariableArray")!.Invoke(null, null)
            : candidate.Original.StartsWith("new int[]", StringComparison.Ordinal) ? fixture.GetMethod("Array")!.Invoke(null, null)
            : candidate.Original.StartsWith("new List", StringComparison.Ordinal) ? fixture.GetMethod("List")!.Invoke(null, null)
            : candidate.Original.StartsWith("new Collection", StringComparison.Ordinal) ? fixture.GetMethod("LegacyCollection")!.Invoke(null, null)
            : candidate.Original.StartsWith("new Dictionary", StringComparison.Ordinal) ? fixture.GetMethod("Map")!.Invoke(null, null)
            : candidate.Original.StartsWith("new SomeClass", StringComparison.Ordinal) ? fixture.GetMethod("Object")!.Invoke(null, null)
            : candidate.Original.Contains("stackalloc", StringComparison.Ordinal) ? fixture.GetMethod("StackArray")!.Invoke(null, null)
            : fixture.GetMethod("Collection")!.Invoke(null, null);
        Assert.That(result, Is.Not.Null);
        Assert.That(result switch
        {
          int[] array => array.Length,
          List<int> list => list.Count,
          System.Collections.ObjectModel.Collection<int> collection => collection.Count,
          Dictionary<int, int> dictionary => dictionary.Count,
          int value => value,
          object item => string.IsNullOrEmpty((string?)item.GetType().GetProperty("Foo")!.GetValue(item)) ? 0 : 1,
          null => -1
        }, Is.Zero,
            $"Initializer mutant did not activate: {candidate.Original} -> {candidate.Replacement}");
      }

      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "Fixture.csproj");
      var testProject = Path.Combine(testsDirectory, "Fixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "Fixture.cs"), File.ReadAllText(source));
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\Fixture.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "InitializerConformanceTests.cs"),
          "using NUnit.Framework; using System.Collections.Generic; using System.Collections.ObjectModel; [TestFixture] public sealed class InitializerConformanceTests { [Test] public void EveryInitializerMutationIsCheckedAgainstOriginalBehavior() { Assert.That(Fixture.Array(), Is.EqualTo(new[] { 1, 2 })); Assert.That(Fixture.VariableArray(), Is.EqualTo(new[] { 1, 2 })); Assert.That(Fixture.List(), Is.EqualTo(new List<int> { 1, 2 })); Assert.That(Fixture.LegacyCollection(), Is.EqualTo(new Collection<int> { 1, 2 })); Assert.That(Fixture.Map(), Is.EqualTo(new Dictionary<int, int> { { 1, 1 } })); Assert.That(Fixture.Object().Foo, Is.EqualTo(\"Bar\")); Assert.That(Fixture.Collection(), Is.EqualTo(new[] { 1, 2 })); Assert.That(Fixture.StackArray(), Is.EqualTo(3)); } }");
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["initialization"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(initializers.Length));
      Assert.That(engineResult.Counters.CoveredMutants, Is.EqualTo(initializers.Length));
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status == MutationStatus.Killed), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status != MutationStatus.Killed)
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "Fixture.cs" && mutant.Line == 1 && mutant.Column > 0), Is.True);
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }

  [Test]
  [Category("EndToEnd")]
  public async Task StatementRemovalMutationsCompileActivateAndExecuteInEngineAsync()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenRemovalParity", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      var target = Path.Combine(root, "instrumented", "Fixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "using System; using System.Collections.Generic; public static class Fixture { public static int Calls; public static int NestedCalls; public static int ReturnValue(int value) { return value; } public static int ThrowValue() { throw new InvalidOperationException(); } public static int ThrowException(Exception exception) { throw exception; } public static int Rethrow() { try { throw new InvalidOperationException(); } catch { throw; } } public static int BreakLoop() { int i = 0; while (i++ < 2) { break; i++; } return i; } public static int ContinueLoop() { int sum = 0; for (int i = 0; i < 3; i++) { if (i == 1) continue; sum += i; } return sum; } public static int GotoTarget() { goto Target; return 1; Target: return 2; } public static void ReturnVoid() { return; Calls++; } public static void CallStatement() { MyMethodCall(); } public static void Nested(bool condition) { if (condition) { NestedCalls++; } } public static void InfiniteLoopBlock() { while (true) { break; } } public static void SwitchBlock(int value) { switch (value) { case 0: { Calls++; break; } } } private static void MyMethodCall() { Calls++; } public static IEnumerable<int> YieldValue() { yield return 1; yield return 2; } public static IEnumerable<int> YieldStop() { yield return 1; yield break; yield return 2; } public struct AssignmentConstructor { public int Value; public AssignmentConstructor(int value) { Value = value; } } }");
      var unsupported = new List<MutationResult>();
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None, unsupported)
          .Where(candidate => candidate.Operator == nameof(MutationCategory.Removal)).ToArray();
      Assert.That(unsupported.Any(mutation => mutation.Operator == nameof(MutationCategory.Removal) &&
          mutation.Diagnostic?.Contains("exception-handling block", StringComparison.Ordinal) == true), Is.True,
          "Blocks that cannot be emptied without breaking method completion must be reported as unsupported.");
      using (var parity = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "benchmarks", "stryker-net-5.0.0-parity.json"))))
      {
        var transformations = parity.RootElement.GetProperty("documentedTransformations").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Removal mutators (_statement_, _block_)" &&
                row.GetProperty("status").GetString() == "supported")
            .Select(row => row.GetProperty("from").GetString()!).ToArray();
        Assert.That(transformations, Has.Length.EqualTo(12));
        foreach (var transformation in transformations)
        {
          var matches = transformation switch
          {
            "void Function() { Age++; }" => candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("return; Calls++;", StringComparison.Ordinal)),
            "int Function() { Age++; return Age; }" => candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("return value;", StringComparison.Ordinal)),
            "return;" => candidates.Any(candidate => candidate.Original == "return;" && candidate.ReplacementToken == "removal:ReturnStatement"),
            "return value;" => candidates.Any(candidate => candidate.Original == "return value;" && candidate.ReplacementToken == "removal:ReturnStatement"),
            "break;" => candidates.Any(candidate => candidate.Original == "break;" && candidate.ReplacementToken == "removal:BreakStatement"),
            "continue;" => candidates.Any(candidate => candidate.Original == "continue;" && candidate.ReplacementToken == "removal:ContinueStatement"),
            "goto;" => candidates.Any(candidate => candidate.Original.StartsWith("goto ", StringComparison.Ordinal) && candidate.ReplacementToken == "removal:GotoStatement"),
            "throw;" => candidates.Any(candidate => candidate.Original == "throw;" && candidate.ReplacementToken == "removal:ThrowStatement"),
            "throw exception;" => candidates.Any(candidate => candidate.Original == "throw exception;" && candidate.ReplacementToken == "removal:ThrowStatement"),
            "yield return value;" => candidates.Any(candidate => candidate.Original.StartsWith("yield return", StringComparison.Ordinal) && candidate.ReplacementToken == "removal:YieldReturnStatement"),
            "yield break;" => candidates.Any(candidate => candidate.Original == "yield break;" && candidate.ReplacementToken == "removal:YieldBreakStatement"),
            "MyMethodCall();" => candidates.Any(candidate => candidate.Original == "MyMethodCall();" && candidate.ReplacementToken == "removal:ExpressionStatement"),
            _ => throw new AssertionException($"No removal fixture mapping for documented transformation: {transformation}")
          };
          Assert.That(matches, Is.True, $"No candidate for documented removal transformation: {transformation}");
        }
        var sourceDerived = parity.RootElement.GetProperty("sourceDerivedCases").EnumerateArray()
            .Where(row => row.GetProperty("category").GetString() == "Mutation / Removal mutators (_statement_, _block_)" &&
                row.GetProperty("status").GetString() == "supported")
            .ToArray();
        Assert.That(sourceDerived, Has.Length.EqualTo(1));
        Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "removal:block" &&
            NormalizeRemoval(candidate.Original) == NormalizeRemoval(sourceDerived[0].GetProperty("from").GetString()!) &&
            NormalizeRemoval(candidate.Replacement) == NormalizeRemoval(sourceDerived[0].GetProperty("to").GetString()!)), Is.True,
            "Missing the pinned BlockMutator's implementation-derived nested-block transformation.");
      }
      static string NormalizeRemoval(string value) => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
      Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original == "{ break; }"), Is.False,
          "Stryker excludes blocks that are directly inside an infinite while loop.");
      Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original == "{ Calls++; break; }"), Is.False,
          "Stryker excludes blocks inside switch sections.");
      Assert.That(candidates.Any(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original == "{ Value = value; }"), Is.False,
          "Stryker protects assignments in struct constructors.");
      Assert.That(candidates.Select(candidate => candidate.ReplacementToken).Distinct(StringComparer.Ordinal),
          Does.Contain("removal:ReturnStatement").And.Contain("removal:BreakStatement").And.Contain("removal:ContinueStatement")
              .And.Contain("removal:GotoStatement").And.Contain("removal:ThrowStatement").And.Contain("removal:YieldReturnStatement")
              .And.Contain("removal:YieldBreakStatement").And.Contain("removal:ExpressionStatement").And.Contain("removal:block"));
      var breakCandidates = candidates.Where(candidate => candidate.Original == "break;").ToArray();
      Assert.That(breakCandidates, Has.Length.EqualTo(2), string.Join(Environment.NewLine, breakCandidates.Select(candidate =>
      {
        var node = CSharpSyntaxTree.ParseText(File.ReadAllText(source)).GetRoot()
            .FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(candidate.Start, candidate.Length));
        return $"At {candidate.Start}: {string.Join(" > ", node.AncestorsAndSelf().Select(ancestor => ancestor.Kind()))}";
      })));
      File.Copy(source, target);
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates.ToList(), root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; public static bool IsActive(int id) => Active == id; public static void Hit(int id) { } }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[] { CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target), CSharpSyntaxTree.ParseText(runtime, parse, "Runtime.cs") };
      var references = PlatformReferences;
      var compilation = CSharpCompilation.Create("RemovalFixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())) + Environment.NewLine + File.ReadAllText(target));
      using var bytes = new MemoryStream();
      var emit = compilation.Emit(bytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      bytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);
      var fixture = assembly.GetType("Fixture")!;
      var active = assembly.GetType("MutationRuntime")!.GetField("Active")!;
      object? Invoke(string name, params object[] args) => fixture.GetMethod(name)!.Invoke(null, args);
      Assert.That(Invoke("ReturnValue", 7), Is.EqualTo(7));
      Assert.That(Invoke("BreakLoop"), Is.EqualTo(1));
      Assert.That(Invoke("ContinueLoop"), Is.EqualTo(2));
      Assert.That(Invoke("GotoTarget"), Is.EqualTo(2));
      Invoke("InfiniteLoopBlock");
      fixture.GetField("Calls")!.SetValue(null, 0);
      Invoke("SwitchBlock", 0);
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.EqualTo(1));
      Assert.Throws<System.Reflection.TargetInvocationException>(() => Invoke("ThrowValue"));
      Assert.That(((IEnumerable<int>)Invoke("YieldValue")!).ToArray(), Is.EqualTo(new[] { 1, 2 }));
      Assert.That(((IEnumerable<int>)Invoke("YieldStop")!).ToArray(), Is.EqualTo(new[] { 1 }));
      fixture.GetField("NestedCalls")!.SetValue(null, 0);
      Invoke("Nested", true);
      Assert.That(fixture.GetField("NestedCalls")!.GetValue(null), Is.EqualTo(1));
      fixture.GetField("Calls")!.SetValue(null, 0);
      Invoke("ReturnVoid");
      Invoke("CallStatement");
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.EqualTo(1));

      void Activate(Candidate candidate) => active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      Activate(candidates.Single(candidate => candidate.Original == "return value;"));
      Assert.That(Invoke("ReturnValue", 7), Is.Zero);
      Activate(candidates.Where(candidate => candidate.Original == "throw new InvalidOperationException();").OrderBy(candidate => candidate.Start).First());
      Assert.That(Invoke("ThrowValue"), Is.Zero);
      Activate(candidates.Single(candidate => candidate.Original == "throw;"));
      Assert.That(Invoke("Rethrow"), Is.Zero);
      Activate(breakCandidates.OrderBy(candidate => candidate.Start).First());
      Assert.That(Invoke("BreakLoop"), Is.EqualTo(3));
      Activate(candidates.Single(candidate => candidate.Original == "continue;"));
      Assert.That(Invoke("ContinueLoop"), Is.EqualTo(3));
      Activate(candidates.Single(candidate => candidate.Original == "goto Target;"));
      Assert.That(Invoke("GotoTarget"), Is.EqualTo(1));
      Activate(candidates.Where(candidate => candidate.Original == "yield return 1;").OrderBy(candidate => candidate.Start).First());
      Assert.That(((IEnumerable<int>)Invoke("YieldValue")!).ToArray(), Is.Empty);
      Activate(candidates.Single(candidate => candidate.Original == "yield break;"));
      Assert.That(((IEnumerable<int>)Invoke("YieldStop")!).ToArray(), Is.EqualTo(new[] { 1, 2 }));
      fixture.GetField("Calls")!.SetValue(null, 0);
      Activate(candidates.Single(candidate => candidate.Original == "return;"));
      Assert.DoesNotThrow(() => Invoke("ReturnVoid"));
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.EqualTo(1));
      fixture.GetField("Calls")!.SetValue(null, 0);
      Activate(candidates.Single(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("return value;", StringComparison.Ordinal)));
      Assert.That(Invoke("ReturnValue", 7), Is.Zero);
      fixture.GetField("Calls")!.SetValue(null, 0);
      Activate(candidates.Single(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("MyMethodCall();", StringComparison.Ordinal)));
      Invoke("CallStatement");
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.Zero);
      Activate(candidates.Single(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original.Contains("yield return 1; yield return 2;", StringComparison.Ordinal)));
      Assert.That(((IEnumerable<int>)Invoke("YieldValue")!).ToArray(), Is.Empty);
      fixture.GetField("NestedCalls")!.SetValue(null, 0);
      Activate(candidates.Single(candidate => candidate.ReplacementToken == "removal:block" && candidate.Original == "{ NestedCalls++; }"));
      Invoke("Nested", true);
      Assert.That(fixture.GetField("NestedCalls")!.GetValue(null), Is.Zero);
      Activate(candidates.Single(candidate => candidate.Original == "MyMethodCall();"));
      fixture.GetField("Calls")!.SetValue(null, 0);
      Invoke("CallStatement");
      Assert.That(fixture.GetField("Calls")!.GetValue(null), Is.Zero);

      var engineRoot = Path.Combine(root, "engine-e2e");
      var productionDirectory = Path.Combine(engineRoot, "Production");
      var testsDirectory = Path.Combine(engineRoot, "Tests");
      Directory.CreateDirectory(productionDirectory);
      Directory.CreateDirectory(testsDirectory);
      File.WriteAllText(Path.Combine(engineRoot, "MutationConformance.slnx"), "<Solution />");
      var productionProject = Path.Combine(productionDirectory, "Fixture.csproj");
      var testProject = Path.Combine(testsDirectory, "Fixture.Tests.csproj");
      File.WriteAllText(productionProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
      File.WriteAllText(Path.Combine(productionDirectory, "Fixture.cs"), File.ReadAllText(source));
      File.WriteAllText(testProject,
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.12.0\" /><PackageReference Include=\"NUnit\" Version=\"5.0.0\" /><PackageReference Include=\"NUnit3TestAdapter\" Version=\"6.3.1\" /><ProjectReference Include=\"..\\Production\\Fixture.csproj\" /></ItemGroup></Project>");
      File.WriteAllText(Path.Combine(testsDirectory, "RemovalConformanceTests.cs"),
          "using System; using NUnit.Framework; using System.Linq; [TestFixture] public sealed class RemovalConformanceTests { [Test] public void EveryRemovalMutationIsCheckedAgainstOriginalBehavior() { Assert.That(Fixture.ReturnValue(7), Is.EqualTo(7)); Assert.Throws<InvalidOperationException>(() => Fixture.ThrowValue()); Assert.Throws<ArgumentException>(() => Fixture.ThrowException(new ArgumentException())); Assert.Throws<InvalidOperationException>(() => Fixture.Rethrow()); Assert.That(Fixture.BreakLoop(), Is.EqualTo(1)); Assert.That(Fixture.ContinueLoop(), Is.EqualTo(2)); Assert.That(Fixture.GotoTarget(), Is.EqualTo(2)); Fixture.Calls = 0; Fixture.SwitchBlock(0); Assert.That(Fixture.Calls, Is.EqualTo(1)); Fixture.Calls = 0; Fixture.ReturnVoid(); Assert.That(Fixture.Calls, Is.Zero); Fixture.Calls = 0; Fixture.CallStatement(); Assert.That(Fixture.Calls, Is.EqualTo(1)); Fixture.NestedCalls = 0; Fixture.Nested(true); Assert.That(Fixture.NestedCalls, Is.EqualTo(1)); Assert.That(Fixture.YieldValue(), Is.EqualTo(new[] { 1, 2 })); Assert.That(Fixture.YieldStop(), Is.EqualTo(new[] { 1 })); } }");
      var engineResult = await MutationEngine.RunAsync(new MutationOptions
      {
        ProjectPath = productionProject,
        TestProjects = [testProject],
        TargetFramework = "net11.0",
        Mutators = new HashSet<string>(["statement-removal"], StringComparer.Ordinal),
        MaxWorkers = 4,
        TimeoutSeconds = 120,
        ExecutionMode = MutationExecutionMode.Isolated,
        OutputDirectory = Path.Combine(engineRoot, "mutation-results")
      });
      Assert.That(engineResult.OverallStatus, Is.EqualTo("Completed"), string.Join(Environment.NewLine, engineResult.Diagnostics));
      Assert.That(engineResult.Mutants, Has.Count.EqualTo(candidates.Length));
      var originalTree = CSharpSyntaxTree.ParseText(File.ReadAllText(source));
      var unreachableCandidates = candidates.Where(candidate =>
      {
        var methodName = originalTree.GetRoot().FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(candidate.Start, candidate.Length))
            .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText;
        return candidate.Original == "return 1;" || candidate.Original == "yield return 2;" && methodName == "YieldStop" ||
            methodName == "InfiniteLoopBlock";
      }).ToArray();
      Assert.That(engineResult.Counters.UncoveredMutants, Is.EqualTo(unreachableCandidates.Length));
      Assert.That(engineResult.Mutants.Where(mutant => mutant.Status == MutationStatus.NoCoverage).Select(mutant => mutant.Original)
          .Order(StringComparer.Ordinal), Is.EqualTo(unreachableCandidates.Select(candidate => candidate.Original).Order(StringComparer.Ordinal)));
      Assert.That(engineResult.Mutants.All(mutant => mutant.Status is MutationStatus.Killed or MutationStatus.Survived or MutationStatus.NoCoverage), Is.True,
          string.Join(Environment.NewLine, engineResult.Mutants.Where(mutant => mutant.Status is not (MutationStatus.Killed or MutationStatus.Survived or MutationStatus.NoCoverage))
              .Select(mutant => $"{mutant.Operator}: {mutant.Original} -> {mutant.Replacement} ({mutant.Status})")));
      Assert.That(engineResult.Mutants.Any(mutant => mutant.Status == MutationStatus.Killed), Is.True);
      Assert.That(engineResult.Mutants.Where(mutant => mutant.Status == MutationStatus.Survived)
          .Select(mutant => $"{mutant.Original} -> {mutant.Replacement}"), Is.EquivalentTo(new[]
          {
            "{ return; Calls++; } -> ;"
          }));
      Assert.That(engineResult.Mutants.All(mutant => Path.GetFileName(mutant.SourceFile) == "Fixture.cs" && mutant.Line == 1 && mutant.Column > 0), Is.True);
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, true);
      }
    }
  }
}
