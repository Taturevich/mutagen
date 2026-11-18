using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal static class MutationRuntimeSource
{
  internal static void WriteRuntimeSources(string workspaceCopy, string solutionRoot, IReadOnlyList<string> productionProjects, IReadOnlyList<Candidate> candidates)
  {
    var siteCount = candidates.Select(candidate => candidate.SiteRuntimeId).Distinct(StringComparer.Ordinal).Count();
    var byProject = candidates.ToLookup(candidate => candidate.OwnerProject ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    foreach (var project in productionProjects)
    {
      var local = byProject[project].Concat(byProject[string.Empty]).ToArray();
      var source = RuntimeSource(siteCount, local);
      var projectDirectory = Path.Combine(workspaceCopy, Path.GetRelativePath(solutionRoot, Path.GetDirectoryName(project)!));
      File.WriteAllText(Path.Combine(projectDirectory, "MutationRuntime.g.cs"), source);
    }
  }

  internal static string RuntimeSource(int siteCount, IReadOnlyList<Candidate>? candidates = null)
  {
    var sharedSiteMutations = (candidates ?? []).GroupBy(candidate => candidate.SiteRuntimeId, StringComparer.Ordinal)
        .Where(group => group.Count() > 1).SelectMany(group => group).ToArray();
    return $$"""
    #nullable enable
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Runtime.CompilerServices;
    internal static class MutationRuntime
    {
      static readonly string ActiveMutationIds = Environment.GetEnvironmentVariable("MUTATION_ACTIVE_ID") ?? string.Empty;
      static readonly int Active = int.TryParse(ActiveMutationIds, out var id) ? id : -1;
      static readonly System.Collections.Generic.HashSet<int>? ActiveSet = Active >= 0 ? null : ActiveMutationIds
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => int.TryParse(value, out var id) ? id : -1).Where(id => id >= 0).ToHashSet();
      // Constant arrays lower to data blobs, keeping the initializer small at large scales.
      // Only shared sites in this assembly need validation; singleton activations allocate no map.
      static readonly int[]? MutantsAtSharedSites = ActiveSet is { Count: > 1 } ? new int[] { {{string.Join(",", sharedSiteMutations.Select(candidate => candidate.MutantRuntimeId))}} } : null;
      static readonly int[]? SitesForSharedMutants = MutantsAtSharedSites is not null ? new int[] { {{string.Join(",", sharedSiteMutations.Select(candidate => candidate.SiteRuntimeId))}} } : null;
      static readonly string ProbePath = Environment.GetEnvironmentVariable("MUTATION_PROBE_FILE") ?? string.Empty;
      static readonly bool PerTestCoverageEnabled = Environment.GetEnvironmentVariable("MUTATION_PER_TEST_COVERAGE") == "1";
      static readonly bool ProbeEnabled = !string.IsNullOrEmpty(ProbePath) || PerTestCoverageEnabled;
      static readonly int[] Hits = ProbeEnabled ? new int[{{siteCount}}] : Array.Empty<int>();
      [ModuleInitializer] internal static void Initialize() { if (ProbeEnabled) { _ = Hits.Length; } }
      static MutationRuntime()
      {
        if (MutantsAtSharedSites is not null)
        {
          var activeSites = new System.Collections.Generic.HashSet<int>();
          for (var index = 0; index < MutantsAtSharedSites.Length; index++)
          {
            if (IsActive(MutantsAtSharedSites[index]) && !activeSites.Add(SitesForSharedMutants![index]))
            {
              throw new InvalidOperationException("Multiple mutations at one site cannot be activated together.");
            }
          }
        }
        if (!ProbeEnabled || PerTestCoverageEnabled) { return; }
        AppDomain.CurrentDomain.ProcessExit += (_,__) =>
        {
          var timer = Stopwatch.StartNew();
          var ids = JsonSerializer.Serialize(Enumerable.Range(0, Hits.Length).Where(index => Volatile.Read(ref Hits[index]) != 0));
          timer.Stop();
          File.AppendAllText(ProbePath, "{\"siteIds\":" + ids + ",\"serializationMs\":" + timer.ElapsedMilliseconds + "}" + Environment.NewLine);
        };
      }
      public static bool IsActive(int id) => Active == id || ActiveSet?.Contains(id) == true;
      public static void Hit(int id) { if (ProbeEnabled && Volatile.Read(ref Hits[id]) == 0) { Interlocked.CompareExchange(ref Hits[id], 1, 0); } }
      public static bool IsActiveAndHit(int siteId, int mutantId) { if (ProbeEnabled) { Hit(siteId); } return IsActive(mutantId); }
      public static int[] GetCoverageData()
      {
        var sites = new System.Collections.Generic.List<int>();
        for (var index = 0; index < Hits.Length; index++)
        {
          if (Volatile.Read(ref Hits[index]) != 0 && Interlocked.Exchange(ref Hits[index], 0) != 0) { sites.Add(index); }
        }
        return sites.ToArray();
      }
      public static T Probe<T>(int id, Func<T> value) { if (ProbeEnabled) { Hit(id); } return value(); }
    }
    """;
  }
}
