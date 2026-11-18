using System.Text.Json;

using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed record CachedCoverageMap(string[] RuntimeSiteIds, string[] Sites, string IntegrityHash);

internal sealed class CoverageMap
{
  private readonly Dictionary<string, string> _sitesByRuntimeId;
  private readonly HashSet<string> _sites;
  private readonly string _mappingIdentity;

  internal CoverageMap(IEnumerable<Candidate> candidates)
  {
    _sitesByRuntimeId = candidates.Select(candidate => (candidate.SiteRuntimeId, candidate.SiteId))
        .Distinct().ToDictionary(pair => pair.SiteRuntimeId, pair => pair.SiteId, StringComparer.Ordinal);
    _sites = _sitesByRuntimeId.Values.ToHashSet(StringComparer.Ordinal);
    _mappingIdentity = string.Join("|", _sitesByRuntimeId.Select(pair => pair.Key + "=" + pair.Value).Order(StringComparer.Ordinal));
  }

  internal sealed record ProbeRead(string[] Sites, long SerializationMs, CoverageStatus Status = CoverageStatus.CoverageSuccess);

  internal async Task<ProbeRead> ReadProbeSitesCached(string path, string testId,
      string[] stableAssemblyIdentities, ArtifactCache cache,
      List<string> diagnostics, CancellationToken cancellationToken)
  {
    // Baseline tests and probe parsing always run. Only the deterministic runtime-site-to-site projection is cached.
    var observed = ReadProbeRuntimeSiteIds(path);
    return await ReadCoverageSitesCached(observed.RuntimeSiteIds, testId, stableAssemblyIdentities,
        cache, diagnostics, cancellationToken, observed.SerializationMs);
  }

  internal async Task<ProbeRead> ReadCoverageSitesCached(string[] runtimeSiteIds, string testId,
      string[] stableAssemblyIdentities, ArtifactCache cache,
      List<string> diagnostics, CancellationToken cancellationToken, long serializationMs = 0)
  {
    var observedRuntimeSiteIds = runtimeSiteIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    var keyValues = new[]
        { "coverage-map-v1", testId, string.Join("|", observedRuntimeSiteIds), _mappingIdentity,
          string.Join("|", stableAssemblyIdentities.Order(StringComparer.OrdinalIgnoreCase)),
          CacheIdentity.EnvironmentFingerprint(), CacheIdentity.EngineVersion(), CacheIdentity.HostVersion() };
    var key = ArtifactCache.ContentKey([], keyValues.Order(StringComparer.Ordinal));
    var cacheRead = await cache.TryReadAsync<CachedCoverageMap>("coverage-maps", testId, key, cancellationToken);
    if (cacheRead.Found &&
        cacheRead.Value is { } cached &&
        cached?.RuntimeSiteIds is not null && cached.Sites is not null &&
        cached.RuntimeSiteIds.SequenceEqual(observedRuntimeSiteIds, StringComparer.Ordinal) &&
        cached.Sites.SequenceEqual(cached.Sites.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
        cached.Sites.All(_sites.Contains) &&
        string.Equals(cached.IntegrityHash, CoverageMapIntegrity(cached.RuntimeSiteIds, cached.Sites), StringComparison.Ordinal))
    {
      diagnostics.Add(cacheRead.Diagnostic);
      return new(cached.Sites, serializationMs);
    }

    diagnostics.Add(cacheRead.Found
        ? $"CacheInvalidated: coverage-maps {key} (cached mapping failed validation)"
        : cacheRead.Diagnostic);
    var sites = MapSites(observedRuntimeSiteIds).Order(StringComparer.Ordinal).ToArray();
    var result = new CachedCoverageMap(observedRuntimeSiteIds, sites, CoverageMapIntegrity(observedRuntimeSiteIds, sites));
    await cache.WriteAsync("coverage-maps", testId, key, result, cancellationToken);
    return new(sites, serializationMs);
  }

  internal static string CoverageMapIntegrity(IEnumerable<string> runtimeSiteIds, IEnumerable<string> sites) =>
      ArtifactCache.ContentKey([], ["coverage-map-record-v1", .. runtimeSiteIds, "sites", .. sites]);

  internal ProbeRead ReadProbeSites(string path)
  {
    var probe = ReadProbeRuntimeSiteIds(path);
    return new(MapSites(probe.RuntimeSiteIds), probe.SerializationMs);
  }

  private string[] MapSites(IEnumerable<string> runtimeSiteIds)
  {
    var ids = runtimeSiteIds.ToHashSet(StringComparer.Ordinal);
    return _sitesByRuntimeId.Where(pair => ids.Contains(pair.Key)).Select(pair => pair.Value).Distinct(StringComparer.Ordinal).ToArray();
  }

  private sealed record ParsedProbe(string[] RuntimeSiteIds, long SerializationMs);

  private static ParsedProbe ReadProbeRuntimeSiteIds(string path)
  {
    if (!File.Exists(path))
    {
      throw new CoverageCollectionException(CoverageStatus.CoverageUnavailable, $"CoverageUnavailable: probe artifact was not produced: {path}");
    }

    try
    {
      var lines = File.ReadAllText(path).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
      if (lines.Length == 0)
      {
        throw new JsonException("Probe artifact is empty.");
      }

      var values = new HashSet<int>();
      long serializationMs = 0;
      foreach (var line in lines)
      {
        using (var document = JsonDocument.Parse(line))
        {
          var root = document.RootElement;
          var sites = root.ValueKind == JsonValueKind.Array
              ? root.Deserialize<int[]>() ?? throw new JsonException("Probe artifact contains null.")
              : root.GetProperty("siteIds").Deserialize<int[]>() ?? throw new JsonException("Probe artifact siteIds contains null.");
          values.UnionWith(sites);
          if (root.ValueKind != JsonValueKind.Array)
          {
            var duration = root.GetProperty("serializationMs").GetInt64();
            if (duration < 0)
            {
              throw new JsonException("Probe artifact has a negative serialization duration.");
            }

            serializationMs += duration;
          }
        }
      }
      return new(values.Select(x => x.ToString()).Order(StringComparer.Ordinal).ToArray(), serializationMs);
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
    {
      throw new CoverageCollectionException(CoverageStatus.CoverageCorrupted, $"CoverageCorrupted: probe artifact is unreadable or corrupted: {path}", ex);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      throw new CoverageCollectionException(CoverageStatus.CoverageCollectionFailed, $"CoverageCollectionFailed: unable to read probe artifact: {path}", ex);
    }
  }
}
