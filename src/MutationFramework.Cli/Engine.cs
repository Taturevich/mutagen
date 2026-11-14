using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MutationFramework.Sdk;

public enum MutationStatus { Pending, Killed, Survived, NoCoverage, CompileError, Unsupported, Timeout, InfrastructureError, Inconclusive }
public sealed record TestIdentity(string Id, string Name, string DisplayName, long DurationMs = 0);
public sealed record MutationResult(string Id, string SiteId, string Operator, string SourceFile, int Line, int Column, string Original, string Replacement, MutationStatus Status, IReadOnlyList<string> CoveringTests, IReadOnlyList<string> KillingTests, long DurationMs = 0, string? Diagnostic = null);
public sealed record PhaseTimings(long DiscoveryMs, long BuildMs, long BaselineMs, long CoverageMs, long MutationGenerationMs, long ExecutionMs, long ReportingMs);
public sealed record MutationRunResult(string OverallStatus, IReadOnlyList<MutationResult> Mutants, int KilledCount, int SurvivedCount, int NoCoverageCount, double? MutationScore, PhaseTimings Timings, long TotalDurationMs, IReadOnlyDictionary<string, IReadOnlyList<string>> TestToSites, IReadOnlyList<string> Diagnostics, string JsonReportPath, string HtmlReportPath);
public sealed class MutationOptions
{
    public required string ProjectPath { get; init; }
    public required IReadOnlyList<string> TestProjects { get; init; }
    public int MaxWorkers { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    public string OutputDirectory { get; init; } = "mutation-results";
    public string Report { get; init; } = "json,html";
    public int TimeoutSeconds { get; init; } = 120;
    public string? TestFilter { get; init; }
    public string? Include { get; init; }
    public string? Exclude { get; init; }
}

public static class MutationEngine
{
    public static async Task<MutationRunResult> RunAsync(MutationOptions options, CancellationToken cancellationToken = default) => await Engine.RunAsync(options, cancellationToken);
}

internal sealed record Candidate(string Id, string SiteId, string Operator, string File, int Start, int Length, int Line, int Column, string Original, string Replacement, string ReplacementToken, string SiteRuntimeId, string MutantRuntimeId);
internal sealed record TestRun(string Name, bool Passed, bool Skipped, long DurationMs, string? Error) { public string ProbePath { get; init; } = ""; }

internal static class Engine
{
    public static async Task<MutationRunResult> RunAsync(MutationOptions opt, CancellationToken ct)
    {
        if (opt.MaxWorkers < 1 || opt.TimeoutSeconds < 1) throw new ArgumentException("Workers and timeout must be positive.");
        string app = Path.GetFullPath(opt.ProjectPath), test = opt.TestProjects.Count > 0 ? Path.GetFullPath(opt.TestProjects[0]) : throw new ArgumentException("At least one NUnit/VSTest project is required.");
        if (!File.Exists(app) || !File.Exists(test)) throw new FileNotFoundException("Project or test project does not exist.");
        var output = Path.GetFullPath(opt.OutputDirectory); Directory.CreateDirectory(output);
        var sw = Stopwatch.StartNew(); var diagnostics = new List<string>();
        long discoveryMs = 0, buildMs = 0, baselineMs = 0, coverageMs = 0, generationMs = 0, executionMs = 0, reportingMs = 0;
        string session = Path.Combine(Path.GetTempPath(), "mutation-framework", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session);
        try
        {
            var timer = Stopwatch.StartNew();
            var sourceFiles = GetCompileFiles(app, ct);
            var candidates = DiscoverCandidates(sourceFiles, app, opt, ct);
            generationMs = timer.ElapsedMilliseconds;
            var appCopy = Path.Combine(session, "app"); var testCopy = Path.Combine(session, "tests");
            CopyProject(Path.GetDirectoryName(app)!, appCopy); CopyProject(Path.GetDirectoryName(test)!, testCopy);
            var clonedApp = Path.Combine(appCopy, Path.GetFileName(app)); var clonedTest = Path.Combine(testCopy, Path.GetFileName(test));
            RewireProjectReference(clonedTest, appCopy, Path.GetFileName(app));
            var mappedFiles = sourceFiles.ToDictionary(f => f, f => Path.Combine(appCopy, Path.GetRelativePath(Path.GetDirectoryName(app)!, f)), StringComparer.OrdinalIgnoreCase);
            RewriteSources(mappedFiles, candidates, Path.GetDirectoryName(app)!);
            File.WriteAllText(Path.Combine(appCopy, "MutationRuntime.g.cs"), RuntimeSource);
            timer.Restart();
            var build = await RunProcess("dotnet", ["build", clonedTest, "-c", "Debug", "--nologo", "-v:q"], session, null, opt.TimeoutSeconds, ct);
            buildMs = timer.ElapsedMilliseconds;
            if (build.ExitCode != 0) throw new InvalidOperationException("Mutation-enabled project build failed: " + build.Output);
            var testDll = Path.Combine(testCopy, "bin", "Debug", "net11.0", Path.GetFileNameWithoutExtension(clonedTest) + ".dll");
            if (!File.Exists(testDll)) throw new InvalidOperationException("Built test assembly not found: " + testDll);
            timer.Restart();
            var listing = await RunProcess("dotnet", ["test", clonedTest, "--no-build", "--list-tests", "--nologo"], session, null, opt.TimeoutSeconds, ct);
            var names = ParseTests(listing.Output).Select(n => n.Contains('(') ? n[..n.IndexOf('(')] : n).Where(n => opt.TestFilter == null || n.Contains(opt.TestFilter, StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (ParseTests(listing.Output).Any(n => n.Contains('('))) diagnostics.Add("NUnit parameterized cases are selected as their containing test method because the adapter does not expose an exact filter for individual case display names.");
            discoveryMs = timer.ElapsedMilliseconds;
            if (listing.ExitCode != 0 || names.Length == 0) throw new InvalidOperationException("Test discovery failed or returned no tests. " + listing.Output);
            var testToSites = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var baselineByName = new Dictionary<string, TestRun>(StringComparer.Ordinal);
            timer.Restart();
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                var run = await ExecuteTest(clonedTest, name, session, opt, null, ct);
                baselineByName[name] = run;
                var sites = ReadProbeSites(run.ProbePath, candidates);
                testToSites[name] = sites;
                if (!run.Passed && !run.Skipped) diagnostics.Add($"Baseline failed: {name}: {run.Error}");
            }
            baselineMs = timer.ElapsedMilliseconds; coverageMs = baselineMs;
            if (baselineByName.Values.Any(x => !x.Passed && !x.Skipped)) throw new BaselineFailureException("Baseline test failures prevent mutation execution.", diagnostics);
            var siteTests = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var pair in testToSites) foreach (var site in pair.Value) { if (!siteTests.TryGetValue(site, out var list)) siteTests[site] = list = []; list.Add(pair.Key); }
            var completed = new MutationResult[candidates.Count];
            int next = -1;
            timer.Restart();
            var workers = Enumerable.Range(0, Math.Min(opt.MaxWorkers, Math.Max(1, candidates.Count))).Select(async _ =>
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref next); if (i >= candidates.Count) return;
                    ct.ThrowIfCancellationRequested(); var c = candidates[i];
                    var relevant = siteTests.GetValueOrDefault(c.SiteId) ?? [];
                    if (relevant.Count == 0) { completed[i] = ToResult(c, MutationStatus.NoCoverage, relevant, [], 0); continue; }
                    var kill = new List<string>(); long duration = 0; var status = MutationStatus.Survived; string? error = null;
                    foreach (var name in relevant)
                    {
                        var run = await ExecuteTest(clonedTest, name, session, opt, c.MutantRuntimeId, ct); duration += run.DurationMs;
                        if (run.Skipped) { status = MutationStatus.Inconclusive; error = "Selected test was skipped."; break; }
                        if (!run.Passed)
                        {
                            // A failed/missing process is a kill only when the test runner completed and reported an assertion/test failure.
                            if (run.Error != null && run.Error.StartsWith("ASSERTION:", StringComparison.Ordinal)) { status = MutationStatus.Killed; kill.Add(name); }
                            else { status = run.Error?.StartsWith("TIMEOUT:", StringComparison.Ordinal) == true ? MutationStatus.Timeout : MutationStatus.InfrastructureError; error = run.Error; break; }
                        }
                    }
                    completed[i] = ToResult(c, status, relevant, kill, duration, error);
                }
            }).ToArray();
            await Task.WhenAll(workers); executionMs = timer.ElapsedMilliseconds;
            timer.Restart();
            var ordered = completed.OrderBy(x => x.SourceFile, StringComparer.Ordinal).ThenBy(x => x.Line).ThenBy(x => x.Column).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var killed = ordered.Count(x => x.Status == MutationStatus.Killed); var survived = ordered.Count(x => x.Status == MutationStatus.Survived); var denominator = killed + survived;
            var timings = new PhaseTimings(discoveryMs, buildMs, baselineMs, coverageMs, generationMs, executionMs, 0);
            var result = new MutationRunResult("Completed", ordered, killed, survived, ordered.Count(x => x.Status == MutationStatus.NoCoverage), denominator == 0 ? null : (double)killed * 100 / denominator, timings, sw.ElapsedMilliseconds, testToSites, diagnostics, Path.Combine(output, "mutation-report.json"), Path.Combine(output, "index.html"));
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true }; jsonOptions.Converters.Add(new JsonStringEnumConverter());
            if (opt.Report.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("json", StringComparer.OrdinalIgnoreCase)) await File.WriteAllTextAsync(result.JsonReportPath, JsonSerializer.Serialize(result, jsonOptions), ct);
            if (opt.Report.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("html", StringComparer.OrdinalIgnoreCase)) await File.WriteAllTextAsync(result.HtmlReportPath, Html(result), ct);
            reportingMs = timer.ElapsedMilliseconds;
            result = result with { Timings = timings with { ReportingMs = reportingMs }, TotalDurationMs = sw.ElapsedMilliseconds };
            if (File.Exists(result.JsonReportPath)) await File.WriteAllTextAsync(result.JsonReportPath, JsonSerializer.Serialize(result, jsonOptions), ct);
            if (File.Exists(result.HtmlReportPath)) await File.WriteAllTextAsync(result.HtmlReportPath, Html(result), ct);
            Console.WriteLine($"Mutants: {ordered.Length}; killed: {killed}; survived: {survived}; no coverage: {result.NoCoverageCount}; score: {(result.MutationScore is null ? "n/a" : result.MutationScore.Value.ToString("F1") + "%")}");
            return result;
        }
        finally { try { Directory.Delete(session, true); } catch { } }
    }

    private static MutationResult ToResult(Candidate c, MutationStatus s, IReadOnlyList<string> tests, IReadOnlyList<string> killing, long ms, string? error = null) => new(c.Id, c.SiteId, c.Operator, c.File, c.Line, c.Column, c.Original, c.Replacement, s, tests, killing, ms, error);

    private static async Task<TestRun> ExecuteTest(string project, string name, string session, MutationOptions opt, string? active, CancellationToken ct)
    {
        string safe = Hash(name)[..12], resultDir = Path.Combine(session, "results", safe + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(resultDir);
        string probe = Path.Combine(resultDir, "probes.json"); var env = new Dictionary<string, string?> { ["MUTATION_PROBE_FILE"] = probe, ["MUTATION_ACTIVE_ID"] = active };
        var timer = Stopwatch.StartNew();
        var p = await RunProcess("dotnet", ["test", project, "--no-build", "--no-restore", "--nologo", "--filter", "Name=" + name, "--logger", "trx;LogFileName=result.trx", "--results-directory", resultDir], session, env, opt.TimeoutSeconds, ct);
        timer.Stop();
        var trx = Directory.Exists(resultDir) ? Directory.GetFiles(resultDir, "result.trx", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (p.TimedOut) return new(name, false, false, timer.ElapsedMilliseconds, "TIMEOUT: test execution timed out.") { ProbePath = probe };
        if (trx == null) return new(name, false, false, timer.ElapsedMilliseconds, "INFRASTRUCTURE: VSTest did not produce a TRX result. " + p.Output) { ProbePath = probe };
        try
        {
            var xml = XDocument.Load(trx); var counters = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "Counters");
            int passed = (int?)counters?.Attribute("passed") ?? 0, failed = ((int?)counters?.Attribute("failed") ?? 0) + ((int?)counters?.Attribute("error") ?? 0), skipped = (int?)counters?.Attribute("notExecuted") ?? 0;
            bool bad = xml.Descendants().Any(x => x.Name.LocalName == "UnitTestResult" && ((string?)x.Attribute("outcome")) is "Failed" or "Error");
            string? err = bad ? "ASSERTION: " + string.Join("\n", xml.Descendants().Where(x => x.Name.LocalName == "Message" || x.Name.LocalName == "StackTrace").Select(x => x.Value).Take(3)) : null;
            if (failed > 0 || bad) return new(name, false, false, timer.ElapsedMilliseconds, err ?? "INFRASTRUCTURE: test failure reported.") { ProbePath = probe };
            if (passed == 0 && skipped > 0) return new(name, false, true, timer.ElapsedMilliseconds, "Test skipped.") { ProbePath = probe };
            if (passed == 0) return new(name, false, false, timer.ElapsedMilliseconds, "INFRASTRUCTURE: test result contains no executed test.") { ProbePath = probe };
            return new(name, true, false, timer.ElapsedMilliseconds, null) { ProbePath = probe };
        }
        catch (Exception ex) { return new(name, false, false, timer.ElapsedMilliseconds, "INFRASTRUCTURE: invalid TRX: " + ex.Message) { ProbePath = probe }; }
    }

    private static async Task<Proc> RunProcess(string exe, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string?>? env, int timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true }; p.Start(); var stdout = p.StandardOutput.ReadToEndAsync(ct); var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct); timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout)); bool timed = false;
        try { await p.WaitForExitAsync(timeoutCts.Token); } catch (OperationCanceledException) { timed = !ct.IsCancellationRequested; try { p.Kill(true); } catch { } if (ct.IsCancellationRequested) throw; }
        var output = await stdout + await stderr; return new(timed ? -1 : p.ExitCode, output, timed);
    }
    private sealed record Proc(int ExitCode, string Output, bool TimedOut);

    private static string[] GetCompileFiles(string project, CancellationToken ct)
    {
        var p = RunProcess("dotnet", ["msbuild", project, "-getItem:Compile", "-nologo"], Path.GetDirectoryName(Path.GetFullPath(project))!, null, 60, ct).GetAwaiter().GetResult();
        if (p.ExitCode != 0) throw new InvalidOperationException("Unable to evaluate project Compile items: " + p.Output);
        using var doc = JsonDocument.Parse(p.Output); return doc.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray().Select(e => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project))!, e.GetProperty("Identity").GetString()!))).Where(File.Exists).Where(f => !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) && !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static List<Candidate> DiscoverCandidates(string[] files, string project, MutationOptions opt, CancellationToken ct)
    {
        var list = new List<Candidate>(); var root = Path.GetDirectoryName(Path.GetFullPath(project))!;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested(); var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (opt.Include != null && !rel.Contains(opt.Include, StringComparison.OrdinalIgnoreCase) || opt.Exclude != null && rel.Contains(opt.Exclude, StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file); var tree = CSharpSyntaxTree.ParseText(text, path: file, options: new CSharpParseOptions(LanguageVersion.Preview));
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                var replacements = node.Kind() switch
                {
                    SyntaxKind.AddExpression => new[] { ("Arithmetic", SyntaxKind.SubtractExpression) },
                    SyntaxKind.SubtractExpression => new[] { ("Arithmetic", SyntaxKind.AddExpression) },
                    SyntaxKind.MultiplyExpression => new[] { ("Arithmetic", SyntaxKind.DivideExpression) },
                    SyntaxKind.GreaterThanExpression => new[] { ("Equality", SyntaxKind.GreaterThanOrEqualExpression), ("Equality", SyntaxKind.LessThanExpression) },
                    SyntaxKind.GreaterThanOrEqualExpression => new[] { ("Equality", SyntaxKind.GreaterThanExpression) },
                    SyntaxKind.LessThanExpression => new[] { ("Equality", SyntaxKind.LessThanOrEqualExpression) },
                    SyntaxKind.EqualsExpression => new[] { ("Equality", SyntaxKind.NotEqualsExpression) },
                    SyntaxKind.NotEqualsExpression => new[] { ("Equality", SyntaxKind.EqualsExpression) },
                    SyntaxKind.LogicalAndExpression => new[] { ("Logical", SyntaxKind.LogicalOrExpression) },
                    SyntaxKind.LogicalOrExpression => new[] { ("Logical", SyntaxKind.LogicalAndExpression) },
                    _ => []
                };
                foreach (var (op, kind) in replacements)
                {
                    string replacementToken = TokenFor(kind), original = node.ToString();
                    string id = Hash(rel + "|" + node.SpanStart + "|" + replacementToken)[..16]; string site = Hash(rel + "|" + node.SpanStart + "|" + node.Span.Length)[..16];
                    int numId = StableNumber(id), siteId = StableNumber(site);
                    var line = tree.GetLineSpan(node.Span).StartLinePosition;
                    list.Add(new(id, site, op, rel, node.SpanStart, node.Span.Length, line.Line + 1, line.Character + 1, original, ReplaceOperator(node, kind), replacementToken, siteId.ToString(), numId.ToString()));
                }
            }
            foreach (var lit in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Where(x => x.IsKind(SyntaxKind.TrueLiteralExpression) || x.IsKind(SyntaxKind.FalseLiteralExpression)))
            {
                var replacement = lit.IsKind(SyntaxKind.TrueLiteralExpression) ? "false" : "true"; string id = Hash(rel + "|" + lit.SpanStart + "|" + replacement)[..16]; string site = Hash(rel + "|" + lit.SpanStart + "|" + lit.Span.Length)[..16]; var line = tree.GetLineSpan(lit.Span).StartLinePosition;
                list.Add(new(id, site, "Boolean", rel, lit.SpanStart, lit.Span.Length, line.Line + 1, line.Character + 1, lit.ToString(), replacement, replacement, StableNumber(site).ToString(), StableNumber(id).ToString()));
            }
        }
        return list.GroupBy(x => (x.File, x.Start, x.Length, x.ReplacementToken)).Select(x => x.First()).OrderBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Start).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    }
    private static string ReplaceOperator(BinaryExpressionSyntax n, SyntaxKind kind) => SyntaxFactory.ParseExpression($"({n.Left}) {TokenFor(kind)} ({n.Right})").ToString();
    private static string TokenFor(SyntaxKind k) => k switch { SyntaxKind.AddExpression => "+", SyntaxKind.SubtractExpression => "-", SyntaxKind.MultiplyExpression => "*", SyntaxKind.DivideExpression => "/", SyntaxKind.GreaterThanExpression => ">", SyntaxKind.GreaterThanOrEqualExpression => ">=", SyntaxKind.LessThanExpression => "<", SyntaxKind.LessThanOrEqualExpression => "<=", SyntaxKind.EqualsExpression => "==", SyntaxKind.NotEqualsExpression => "!=", SyntaxKind.LogicalAndExpression => "&&", SyntaxKind.LogicalOrExpression => "||", _ => throw new ArgumentOutOfRangeException(nameof(k)) };
    private static int StableNumber(string id) => (int)(Convert.ToUInt32(id[..8], 16) & 0x7fffffff);

    private static void RewriteSources(Dictionary<string, string> files, List<Candidate> candidates, string projectRoot)
    {
        foreach (var (original, target) in files)
        {
            if (!File.Exists(target)) continue;
            var text = File.ReadAllText(target); var tree = CSharpSyntaxTree.ParseText(text, path: target, options: new CSharpParseOptions(LanguageVersion.Preview));
            var local = candidates.Where(c => string.Equals(c.File, Path.GetRelativePath(projectRoot, original).Replace('\\','/'), StringComparison.OrdinalIgnoreCase)).ToArray();
            var root = tree.GetRoot(); var rewritten = new SchemaRewriter(local).Visit(root)!;
            File.WriteAllText(target, rewritten.ToFullString());
        }
    }

    private sealed class SchemaRewriter(IReadOnlyList<Candidate> candidates) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            var ops = candidates.Where(c => c.Start == node.SpanStart && c.Length == node.Span.Length && c.Operator != "Boolean").ToArray();
            ExpressionSyntax result = visited;
            foreach (var c in ops) { var mut = SyntaxFactory.ParseExpression($"({visited.Left}) {c.ReplacementToken} ({visited.Right})"); result = SyntaxFactory.ConditionalExpression(SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({c.MutantRuntimeId})"), SyntaxFactory.ParenthesizedExpression(mut), SyntaxFactory.ParenthesizedExpression(result)); }
            // Build probe wrapper without reparsing user expression.
            if (ops.Length > 0) result = SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"), SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(SyntaxFactory.ParseExpression(ops[0].SiteRuntimeId)), SyntaxFactory.Argument(result) })));
            return result.WithTriviaFrom(node);
        }
        public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            var c = candidates.FirstOrDefault(x => x.Operator == "Boolean" && x.Start == node.SpanStart); if (c == null) return base.VisitLiteralExpression(node);
            var expr = (ExpressionSyntax)SyntaxFactory.ParseExpression($"MutationRuntime.IsActive({c.MutantRuntimeId}) ? {c.Replacement} : {node}").WithTriviaFrom(node);
            return SyntaxFactory.InvocationExpression(SyntaxFactory.ParseExpression("MutationRuntime.Probe"), SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(SyntaxFactory.ParseExpression(c.SiteRuntimeId)), SyntaxFactory.Argument(expr) }))).WithTriviaFrom(node);
        }
    }

    private static string RuntimeSource => "using System; using System.Collections.Concurrent; using System.IO; using System.Text.Json; internal static class MutationRuntime { static readonly int Active = int.TryParse(Environment.GetEnvironmentVariable(\"MUTATION_ACTIVE_ID\"), out var id) ? id : -1; static readonly ConcurrentDictionary<int, byte> Hits = new(); static MutationRuntime() { AppDomain.CurrentDomain.ProcessExit += (_,__) => { var p=Environment.GetEnvironmentVariable(\"MUTATION_PROBE_FILE\"); if(!string.IsNullOrEmpty(p)) try { File.WriteAllText(p, JsonSerializer.Serialize(Hits.Keys)); } catch {} }; } public static bool IsActive(int id) => Active == id; public static T Probe<T>(int id,T value) { Hits.TryAdd(id,0); return value; } }";

    private static string[] ReadProbeSites(string path, List<Candidate> c) { try { var ids = JsonSerializer.Deserialize<int[]>(File.ReadAllText(path))!.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal); return c.Where(x => ids.Contains(x.SiteRuntimeId)).Select(x => x.SiteId).Distinct(StringComparer.Ordinal).ToArray(); } catch { return []; } }
    private static string[] ParseTests(string text)
    {
        var result = new List<string>(); bool reading = false;
        foreach (var raw in text.Split('\n')) { var line = raw.Trim(); if (line.StartsWith("The following Tests are available:", StringComparison.OrdinalIgnoreCase)) { reading = true; continue; } if (reading && line.Length > 0 && !line.StartsWith("Test run", StringComparison.OrdinalIgnoreCase)) result.Add(line.TrimEnd('\r')); }
        return result.ToArray();
    }
    private static void CopyProject(string from, string to) { Directory.CreateDirectory(to); foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories).Where(d => d.Split(Path.DirectorySeparatorChar).All(x => !string.Equals(x, "bin", StringComparison.OrdinalIgnoreCase) && !string.Equals(x, "obj", StringComparison.OrdinalIgnoreCase)))) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir))); foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories).Where(f => f.Split(Path.DirectorySeparatorChar).All(x => !string.Equals(x, "bin", StringComparison.OrdinalIgnoreCase) && !string.Equals(x, "obj", StringComparison.OrdinalIgnoreCase)))) { var dest = Path.Combine(to, Path.GetRelativePath(from, file)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest, true); } }
    private static void RewireProjectReference(string testProject, string appDir, string appProjectName) { var x = XDocument.Load(testProject); var refs = x.Descendants().Where(e => e.Name.LocalName == "ProjectReference").ToArray(); foreach (var r in refs) { var include = (string?)r.Attribute("Include"); if (include != null && Path.GetFileName(include).Equals(appProjectName, StringComparison.OrdinalIgnoreCase)) r.SetAttributeValue("Include", Path.Combine(appDir, appProjectName)); } x.Save(testProject); }
    private static string Html(MutationRunResult r) { static string E(string s) => System.Net.WebUtility.HtmlEncode(s); var t = r.Timings; var b = new StringBuilder("<!doctype html><meta charset=utf-8><title>Mutation report</title><style>body{font:15px system-ui;max-width:1100px;margin:2rem auto}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccc;padding:.5rem;text-align:left}code{white-space:pre-wrap}.Killed{color:#a00}.Survived{color:#087}</style>"); b.Append($"<h1>Mutation report</h1><p>Score: {(r.MutationScore?.ToString("F1") + "%" ?? "n/a")} | Total: {r.Mutants.Count} | Killed: {r.KilledCount} | Survived: {r.SurvivedCount} | No coverage: {r.NoCoverageCount} | Total duration: {r.TotalDurationMs} ms</p><p>Phases (ms): discovery {t.DiscoveryMs}; build {t.BuildMs}; baseline + coverage {t.BaselineMs}; generation {t.MutationGenerationMs}; execution {t.ExecutionMs}; reporting {t.ReportingMs}.</p><table><thead><tr><th>Status</th><th>Source</th><th>Operator</th><th>Transformation</th><th>Tests</th></tr></thead><tbody>"); foreach (var m in r.Mutants) b.Append($"<tr><td class='{m.Status}'>{m.Status}</td><td>{E(m.SourceFile)}:{m.Line}:{m.Column}</td><td>{E(m.Operator)}</td><td><code>{E(m.Original)} → {E(m.Replacement)}</code></td><td>{E(string.Join(", ", m.CoveringTests))}</td></tr>"); return b.Append("</tbody></table>").ToString(); }
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
internal sealed class BaselineFailureException(string message, IReadOnlyList<string> errors) : Exception(message) { public IReadOnlyList<string> Errors { get; } = errors; }
