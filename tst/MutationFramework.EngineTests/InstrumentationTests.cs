using MutationFramework.Sdk;
using NUnit.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Linq.Expressions;

namespace MutationFramework.EngineTests;

using static MutationFramework.EngineTests.TestWorkspace;

[TestFixture]
public sealed class InstrumentationTests
{
  [Test]
  [Category("Component")]
  public void InlineSiteProbesPreserveBoundDynamicCheckedAndExpressionTreeSemantics()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenInlineSiteProbe", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var project = Path.Combine(root, "Fixture.csproj");
      var source = Path.Combine(root, "Fixture.cs");
      var target = Path.Combine(root, "instrumented", "Fixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, """
          using System;
          using System.Linq.Expressions;
          public readonly record struct Score(int Value)
          {
            public static Score operator +(Score left, Score right) => new(left.Value + right.Value);
            public static Score operator -(Score left, Score right) => new(left.Value - right.Value);
          }
          public static class Fixture
          {
            public static readonly System.Collections.Generic.List<string> OperandOrder = new();
            public static int LeftValue = 5;
            public static int RightValue = 2;
            public static Score ReadLeftScore() { OperandOrder.Add("left"); return new(2); }
            public static Score ReadRightScore() { OperandOrder.Add("right"); return new(5); }
            public static dynamic ReadLeftDynamic() { OperandOrder.Add("left"); return 10; }
            public static dynamic ReadRightDynamic() { OperandOrder.Add("right"); return 3; }
            public static int ReadLeftInt() { OperandOrder.Add("left"); return LeftValue; }
            public static int ReadRightInt() { OperandOrder.Add("right"); return RightValue; }
            public static Score Combine() => ReadLeftScore() + ReadRightScore();
            public static dynamic DynamicCombine() => ReadLeftDynamic() + ReadRightDynamic();
            public static int CheckedAdd() => checked(ReadLeftInt() + ReadRightInt());
            public static int OffsetCalls;
            public static int NextOffset() { OffsetCalls++; return 3; }
            public static Expression<Func<int, int>> Tree() => value => value + NextOffset();
          }
          """);

      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), [], CancellationToken.None);
      var userDefined = candidates.Single(candidate => candidate.Original == "ReadLeftScore() + ReadRightScore()" && candidate.Operator == "Arithmetic");
      var dynamic = candidates.Single(candidate => candidate.Original == "ReadLeftDynamic() + ReadRightDynamic()" && candidate.Operator == "Arithmetic");
      var checkedArithmetic = candidates.Single(candidate => candidate.Original == "ReadLeftInt() + ReadRightInt()" && candidate.Operator == "Arithmetic");
      var expressionTree = candidates.Single(candidate => candidate.Original == "value + NextOffset()" && candidate.Operator == "Arithmetic");
      Assert.That(checkedArithmetic.UseInlineSiteProbe, Is.True);
      Assert.That(expressionTree.UseInlineSiteProbe, Is.True);
      Assert.That(userDefined.UseInlineSiteProbe, Is.False);
      Assert.That(dynamic.UseInlineSiteProbe, Is.False);

      File.Copy(source, target);
      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      const string runtime = "public static class MutationRuntime { public static int Active = -1; static readonly System.Collections.Generic.List<int> Sites = new(); public static bool IsActive(int id) => Active == id; public static void Hit(int id) { if (!Sites.Contains(id)) Sites.Add(id); } public static bool IsActiveAndHit(int siteId, int id) { Hit(siteId); return IsActive(id); } public static T Probe<T>(int id, System.Func<T> value) { Hit(id); return value(); } public static void Clear() => Sites.Clear(); public static bool HasSite(int id) => Sites.Contains(id); }";
      var parse = new CSharpParseOptions(LanguageVersion.Preview);
      var trees = new[]
      {
        CSharpSyntaxTree.ParseText(File.ReadAllText(target), parse, target),
        CSharpSyntaxTree.ParseText(runtime, parse, "MutationRuntime.cs")
      };
      var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
          .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
      var compilation = CSharpCompilation.Create("InlineSiteProbeFixture", trees, references,
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
      var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
      Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
      using var assemblyBytes = new MemoryStream();
      var emit = compilation.Emit(assemblyBytes);
      Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
      assemblyBytes.Position = 0;
      var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(assemblyBytes);
      var fixture = assembly.GetType("Fixture")!;
      var runtimeType = assembly.GetType("MutationRuntime")!;
      var active = runtimeType.GetField("Active")!;
      var clear = runtimeType.GetMethod("Clear")!;
      var hasSite = runtimeType.GetMethod("HasSite")!;
      var operandOrder = (List<string>)fixture.GetField("OperandOrder")!.GetValue(null)!;
      bool Saw(Candidate candidate) => (bool)hasSite.Invoke(null, [int.Parse(candidate.SiteRuntimeId, System.Globalization.CultureInfo.InvariantCulture)])!;
      void Activate(Candidate candidate) => active.SetValue(null, int.Parse(candidate.MutantRuntimeId, System.Globalization.CultureInfo.InvariantCulture));
      void Deactivate() => active.SetValue(null, -1);

      var originalScore = fixture.GetMethod("Combine")!.Invoke(null, null)!;
      Assert.That(originalScore.GetType().GetProperty("Value")!.GetValue(originalScore), Is.EqualTo(7));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Deactivate();
      Activate(userDefined);
      operandOrder.Clear();
      var mutatedScore = fixture.GetMethod("Combine")!.Invoke(null, null)!;
      Assert.That(mutatedScore.GetType().GetProperty("Value")!.GetValue(mutatedScore), Is.EqualTo(-3));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Assert.That(Saw(userDefined), Is.True);

      Deactivate();
      operandOrder.Clear();
      Assert.That(fixture.GetMethod("DynamicCombine")!.Invoke(null, null), Is.EqualTo(13));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Activate(dynamic);
      operandOrder.Clear();
      Assert.That(fixture.GetMethod("DynamicCombine")!.Invoke(null, null), Is.EqualTo(7));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Assert.That(Saw(dynamic), Is.True);

      Deactivate();
      operandOrder.Clear();
      var leftValue = fixture.GetField("LeftValue")!;
      var rightValue = fixture.GetField("RightValue")!;
      void SetCheckedOperands(int left, int right) { leftValue.SetValue(null, left); rightValue.SetValue(null, right); }
      object? InvokeChecked() => fixture.GetMethod("CheckedAdd")!.Invoke(null, null);
      SetCheckedOperands(5, 2);
      Assert.That(InvokeChecked(), Is.EqualTo(7));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      clear.Invoke(null, null);
      operandOrder.Clear();
      SetCheckedOperands(int.MaxValue, 1);
      var originalOverflow = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
          InvokeChecked());
      Assert.That(originalOverflow!.InnerException, Is.TypeOf<OverflowException>());
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Assert.That(Saw(checkedArithmetic), Is.True, "The original checked site is recorded before evaluation throws.");
      Activate(checkedArithmetic);
      operandOrder.Clear();
      SetCheckedOperands(5, 2);
      Assert.That(InvokeChecked(), Is.EqualTo(3));
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      clear.Invoke(null, null);
      operandOrder.Clear();
      SetCheckedOperands(int.MaxValue, -1);
      var mutatedOverflow = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
          InvokeChecked());
      Assert.That(mutatedOverflow!.InnerException, Is.TypeOf<OverflowException>());
      Assert.That(operandOrder, Is.EqualTo(new[] { "left", "right" }));
      Assert.That(Saw(checkedArithmetic), Is.True, "The active checked site is recorded before the replacement throws.");

      Deactivate();
      clear.Invoke(null, null);
      var offsetCalls = fixture.GetField("OffsetCalls")!;
      offsetCalls.SetValue(null, 0);
      var expression = (LambdaExpression)fixture.GetMethod("Tree")!.Invoke(null, null)!;
      Assert.That(Saw(expressionTree), Is.False, "Creating an expression tree does not count as evaluating its mutation site.");
      Assert.That(expression.Compile().DynamicInvoke(4), Is.EqualTo(7));
      Assert.That(offsetCalls.GetValue(null), Is.EqualTo(1));
      Assert.That(Saw(expressionTree), Is.True);
      Activate(expressionTree);
      clear.Invoke(null, null);
      offsetCalls.SetValue(null, 0);
      var mutatedExpression = (LambdaExpression)fixture.GetMethod("Tree")!.Invoke(null, null)!;
      Assert.That(offsetCalls.GetValue(null), Is.Zero, "Building a mutated expression tree defers operand evaluation.");
      Assert.That(mutatedExpression.Compile().DynamicInvoke(4), Is.EqualTo(1));
      Assert.That(offsetCalls.GetValue(null), Is.EqualTo(1));
      Assert.That(Saw(expressionTree), Is.True);
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
  [Category("Component")]
  public void ProbeReaderDistinguishesEmptySuccessFromMissingAndCorruptArtifacts()
  {
    var candidate = new Candidate("mutant", "site", "Equality", "sample.cs", 0, 1, 1, 1, "a > b", "a >= b", ">=", "0", "0");
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
    try
    {
      var missing = Assert.Throws<CoverageCollectionException>(() => new CoverageMap([candidate]).ReadProbeSites(path));
      Assert.That(missing!.Status, Is.EqualTo(CoverageStatus.CoverageUnavailable));
      File.WriteAllText(path, "not-json");
      var corrupt = Assert.Throws<CoverageCollectionException>(() => new CoverageMap([candidate]).ReadProbeSites(path));
      Assert.That(corrupt!.Status, Is.EqualTo(CoverageStatus.CoverageCorrupted));
      File.WriteAllText(path, "{\"siteIds\":[],\"serializationMs\":0}");
      Assert.That(new CoverageMap([candidate]).ReadProbeSites(path).Sites, Is.Empty);
      File.WriteAllText(path, "{\"siteIds\":[0],\"serializationMs\":2}");
      var hit = new CoverageMap([candidate]).ReadProbeSites(path);
      Assert.That(hit.Sites, Is.EqualTo(new[] { "site" }));
      Assert.That(hit.SerializationMs, Is.EqualTo(2));
    }
    finally
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
  }

  [Test]
  [Category("Component")]
  public void MutationRuntimeSkipsCoverageHitTrackingWhenProbeCollectionIsDisabled()
  {
    var runtime = MutationRuntimeSource.RuntimeSource(3);
    Assert.That(runtime, Does.Contain("ProbeEnabled = !string.IsNullOrEmpty(ProbePath)"));
    Assert.That(runtime, Does.Contain("ProbeEnabled ? new int[3] : Array.Empty<int>()"));
    Assert.That(runtime, Does.Contain("if (ProbeEnabled && Volatile.Read(ref Hits[id]) == 0) { Interlocked.CompareExchange(ref Hits[id], 1, 0); }"));
    Assert.That(runtime, Does.Contain("if (ProbeEnabled) { Hit(id); } return value();"));
    Assert.That(runtime, Does.Contain("ActiveSet?.Contains(id) == true"));
    Assert.That(runtime, Does.Contain("if (!ProbeEnabled || PerTestCoverageEnabled) { return; }"));
    Assert.That(runtime, Does.Contain("static readonly bool ProbeEnabled = !string.IsNullOrEmpty(ProbePath) || PerTestCoverageEnabled;"));

    var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
    var tree = CSharpSyntaxTree.ParseText(runtime, parseOptions);
    var platformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    var compilation = CSharpCompilation.Create("ProbeGateFixture_" + Guid.NewGuid().ToString("N"), [tree], platformReferences,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using var assemblyBytes = new MemoryStream();
    var emit = compilation.Emit(assemblyBytes);
    Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

    var originalProbePath = Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE");
    try
    {
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", null);
      var assembly = System.Reflection.Assembly.Load(assemblyBytes.ToArray());
      var runtimeType = assembly.GetType("MutationRuntime", throwOnError: true)!;
      var enabled = (bool)runtimeType.GetField("ProbeEnabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
      Assert.That(enabled, Is.False);
      Assert.DoesNotThrow(() => runtimeType.GetMethod("Hit")!.Invoke(null, [0]));
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", originalProbePath);
    }
  }

  [Test]
  [Category("Component")]
  public void MutationRuntimeRecordsConcurrentProbeHitsOnceWithoutLosingSites()
  {
    var runtime = MutationRuntimeSource.RuntimeSource(3);
    var tree = CSharpSyntaxTree.ParseText(runtime, new CSharpParseOptions(LanguageVersion.Preview));
    var platformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    var compilation = CSharpCompilation.Create("ConcurrentProbeFixture_" + Guid.NewGuid().ToString("N"), [tree], platformReferences,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using var assemblyBytes = new MemoryStream();
    var emit = compilation.Emit(assemblyBytes);
    Assert.That(emit.Success, Is.True, string.Join(Environment.NewLine,
        emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

    var originalProbePath = Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE");
    var probePath = Path.Combine(Path.GetTempPath(), "MutagenConcurrentProbe_" + Guid.NewGuid().ToString("N") + ".jsonl");
    try
    {
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", probePath);
      var assembly = System.Reflection.Assembly.Load(assemblyBytes.ToArray());
      var runtimeType = assembly.GetType("MutationRuntime", throwOnError: true)!;
      var hit = runtimeType.GetMethod("Hit")!.CreateDelegate<Action<int>>();
      var drain = runtimeType.GetMethod("GetCoverageData")!.CreateDelegate<Func<int[]>>();
      var observed = new System.Collections.Concurrent.ConcurrentBag<int>();
      Parallel.Invoke(
          () => Parallel.For(0, 100_000, index => hit(index % 2)),
          () =>
          {
            for (var index = 0; index < 1_000; index++)
            {
              foreach (var site in drain()) { observed.Add(site); }
            }
          });
      foreach (var site in drain()) { observed.Add(site); }
      Assert.That(observed, Does.Contain(0));
      Assert.That(observed, Does.Contain(1));

      var hits = (int[])runtimeType.GetField("Hits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
          .GetValue(null)!;
      Assert.That(hits, Is.EqualTo(new[] { 0, 0, 0 }));
    }
    finally
    {
      Environment.SetEnvironmentVariable("MUTATION_PROBE_FILE", originalProbePath);
    }
  }

  [Test]
  [Category("Component")]
  public void StatementEntryProbesAvoidLambdaForWholeReturnExpression()
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenProbeRewrite", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
      var source = Path.Combine(root, "ProbeFixture.cs");
      var project = Path.Combine(root, "ProbeFixture.csproj");
      var target = Path.Combine(root, "instrumented", "ProbeFixture.cs");
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllText(project, "<Project />");
      File.WriteAllText(source, "public static class ProbeFixture { public static bool Check(System.Func<int> value, System.Func<bool> enabled) { return value() > 0 && enabled(); } }");
      File.Copy(source, target);
      var diagnostics = new List<string>();
      var candidates = MutationDiscovery.DiscoverCandidates([source], project, Options(1), diagnostics, CancellationToken.None);

      MutationSchemaBuilder.RewriteSources(new Dictionary<string, string> { [source] = target }, candidates, root, root);
      var rewritten = File.ReadAllText(target);
      var returnRoot = candidates.First(candidate => candidate.Original.Contains("&&", StringComparison.Ordinal));
      var nestedComparison = candidates.First(candidate => candidate.Operator == "Equality");

      Assert.That(rewritten, Does.Contain($"MutationRuntime.Hit({returnRoot.SiteRuntimeId})"));
      Assert.That(rewritten, Does.Contain($"MutationRuntime.Probe({nestedComparison.SiteRuntimeId}"));
      Assert.That(rewritten.IndexOf($"MutationRuntime.Hit({returnRoot.SiteRuntimeId})", StringComparison.Ordinal),
          Is.LessThan(rewritten.IndexOf("return", StringComparison.Ordinal)));
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
