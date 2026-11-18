using System.Text.Json;
using System.Text.Json.Nodes;

using MutationFramework.Sdk;

using NUnit.Framework;

namespace MutationFramework.EngineTests;

[TestFixture]
public sealed class CacheMetadataIntegrityTests
{
  [TestCase("payload", "integrity")]
  [TestCase("identity", "identity")]
  [TestCase("schema", "schema version")]
  [TestCase("legacy", "legacy unversioned")]
  [TestCase("malformed", "malformed")]
  [Category("Component")]
  public async Task InvalidMetadataIsRecomputedWithoutChangingItsContentKey(string corruption, string expectedReason)
  {
    var root = Path.Combine(Path.GetTempPath(), "MutagenCacheIntegrity", Guid.NewGuid().ToString("N"));
    const string phase = "integrity-test";
    const string identity = "same-logical-input";
    var key = ArtifactCache.ContentKey([], ["same-input-key"]);
    var cache = new ArtifactCache(root);
    try
    {
      await cache.WriteAsync(phase, identity, key, "original");
      Assert.That((await cache.TryReadAsync<string>(phase, identity, key)).Value, Is.EqualTo("original"));
      var path = Path.Combine(root, phase, key + ".json");
      var metadata = JsonNode.Parse(File.ReadAllText(path))!;
      switch (corruption)
      {
        case "payload":
          metadata["Payload"] = "tampered-but-valid-json";
          break;
        case "identity":
          metadata["ContentKey"] = ArtifactCache.ContentKey([], ["another-input"]);
          break;
        case "schema":
          metadata["$mutagen-cache"] = 2;
          break;
      }
      File.WriteAllText(path, corruption switch
      {
        "legacy" => JsonSerializer.Serialize("original"),
        "malformed" => "{",
        _ => metadata.ToJsonString()
      });

      var invalid = await cache.TryReadAsync<string>(phase, identity, key);
      Assert.That(invalid.Found, Is.False);
      Assert.That(invalid.Diagnostic, Does.Contain(expectedReason));
      Assert.That(File.Exists(path), Is.True, "An invalid artifact is retained until safe recomputation replaces it.");
      await cache.WriteAsync(phase, identity, key, "recomputed");
      var repaired = await cache.TryReadAsync<string>(phase, identity, key);
      Assert.That(repaired.Found, Is.True);
      Assert.That(repaired.Value, Is.EqualTo("recomputed"));
      Assert.That(File.ReadAllText(Directory.GetFiles(Path.Combine(root, phase, "index"), "*.json").Single()), Is.EqualTo(key));
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
