using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MutationFramework.Sdk;

internal readonly record struct ArtifactCacheRead<T>(bool Found, T? Value, string Diagnostic);

internal sealed class ArtifactCache(string root)
{
  private const int MetadataSchemaVersion = 1;
  private sealed record MetadataEnvelope([property: JsonPropertyName("$mutagen-cache")] int SchemaVersion,
      string ContentKey, JsonElement Payload, string IntegrityHash);
  private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
  private readonly string _root = Path.GetFullPath(root);

  public async Task<ArtifactCacheRead<T>> TryReadAsync<T>(string phase, string logicalIdentity, string contentKey,
      CancellationToken cancellationToken = default)
  {
    var logicalPath = Path.Combine(_root, phase, "index", CacheIdentity.Hash(logicalIdentity) + ".json");
    var artifactPath = Path.Combine(_root, phase, contentKey + ".json");
    string? previousKey;
    using (await AcquireWriteLockAsync(logicalPath, cancellationToken))
    {
      previousKey = File.Exists(logicalPath) ? await ReadSharedTextAsync(logicalPath, cancellationToken) : null;
    }

    if (string.Equals(previousKey, contentKey, StringComparison.Ordinal))
    {
      try
      {
        string? artifact;
        using (await AcquireWriteLockAsync(artifactPath, cancellationToken))
        {
          artifact = File.Exists(artifactPath) ? await ReadSharedTextAsync(artifactPath, cancellationToken) : null;
        }
        if (artifact is not null)
        {
          return ReadMetadata<T>(artifact, phase, contentKey);
        }
      }
      catch (JsonException)
      {
        return InvalidMetadata<T>(phase, contentKey, "metadata is malformed or has an incompatible payload");
      }
    }

    var diagnostic = previousKey is null
        ? $"CacheMiss: {phase} {contentKey} (no prior artifact)"
        : $"CacheInvalidated: {phase} prior={previousKey} current={contentKey}";
    return new(false, default, diagnostic);
  }

  public async Task WriteAsync<T>(string phase, string logicalIdentity, string contentKey, T value,
      CancellationToken cancellationToken = default)
  {
    var phaseRoot = Path.Combine(_root, phase);
    var logicalPath = Path.Combine(phaseRoot, "index", CacheIdentity.Hash(logicalIdentity) + ".json");
    var artifactPath = Path.Combine(phaseRoot, contentKey + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(logicalPath)!);
    var payload = JsonSerializer.SerializeToElement(value);
    var envelope = new MetadataEnvelope(MetadataSchemaVersion, contentKey, payload, CacheIdentity.Hash(payload.GetRawText()));
    await WriteAtomicAsync(artifactPath, JsonSerializer.Serialize(envelope), cancellationToken);
    await WriteAtomicAsync(logicalPath, contentKey, cancellationToken);
  }

  private static ArtifactCacheRead<T> ReadMetadata<T>(string artifact, string phase, string contentKey)
  {
    using var document = JsonDocument.Parse(artifact);
    if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("$mutagen-cache", out _))
    {
      // Version 0 stored raw payloads without an integrity check. Recompute rather than
      // promoting unverifiable metadata; indexes, keys and content-addressed blobs remain valid.
      return InvalidMetadata<T>(phase, contentKey, "legacy unversioned metadata requires verified recomputation");
    }
    var envelope = document.RootElement.Deserialize<MetadataEnvelope>(JsonOptions);
    if (envelope is null || envelope.SchemaVersion != MetadataSchemaVersion)
    {
      return InvalidMetadata<T>(phase, contentKey, "unsupported metadata schema version");
    }
    if (!string.Equals(envelope.ContentKey, contentKey, StringComparison.Ordinal) || envelope.Payload.ValueKind == JsonValueKind.Undefined)
    {
      return InvalidMetadata<T>(phase, contentKey, "metadata content identity or payload is invalid");
    }
    if (!string.Equals(envelope.IntegrityHash, CacheIdentity.Hash(envelope.Payload.GetRawText()), StringComparison.Ordinal))
    {
      return InvalidMetadata<T>(phase, contentKey, "metadata payload integrity check failed");
    }
    var value = envelope.Payload.Deserialize<T>(JsonOptions);
    return value is null ? InvalidMetadata<T>(phase, contentKey, "metadata payload is null")
        : new(true, value, $"CacheHit: {phase} {contentKey}");
  }

  private static ArtifactCacheRead<T> InvalidMetadata<T>(string phase, string contentKey, string reason) =>
      new(false, default, $"CacheInvalidated: {phase} {contentKey} ({reason})");

  public async Task<string> StoreBlobAsync(string phase, string sourcePath, CancellationToken cancellationToken = default)
  {
    var contentKey = await HashFileAsync(sourcePath, cancellationToken);
    var blobPath = BlobPath(phase, contentKey);
    Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
    using var writeLock = await AcquireWriteLockAsync(blobPath, cancellationToken);
    if (File.Exists(blobPath) && string.Equals(await HashFileAsync(blobPath, cancellationToken), contentKey, StringComparison.Ordinal))
    {
      return contentKey;
    }

    var temporaryPath = blobPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
      await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
      await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
      {
        await source.CopyToAsync(destination, cancellationToken);
      }

      cancellationToken.ThrowIfCancellationRequested();
      if (!string.Equals(await HashFileAsync(temporaryPath, cancellationToken), contentKey, StringComparison.Ordinal))
      {
        throw new IOException($"Compilation artifact '{sourcePath}' changed while it was being cached.");
      }

      File.Move(temporaryPath, blobPath, true);
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }

    return contentKey;
  }

  public async Task<bool> TryCopyBlobAsync(string phase, string contentKey, string destinationPath,
      CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrEmpty(contentKey) || contentKey.Length != 64 || contentKey.Any(character => !Uri.IsHexDigit(character)))
    {
      return false;
    }

    var blobPath = BlobPath(phase, contentKey);
    using var readLock = await AcquireWriteLockAsync(blobPath, cancellationToken);
    if (!File.Exists(blobPath) || !string.Equals(await HashFileAsync(blobPath, cancellationToken), contentKey, StringComparison.Ordinal))
    {
      return false;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    await using var source = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
    await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
    await source.CopyToAsync(destination, cancellationToken);
    return true;
  }

  public static string ContentKey(IEnumerable<string> inputs, IEnumerable<string> values)
  {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var value in values)
    {
      Append(hash, "value:" + value);
    }
    foreach (var path in inputs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
    {
      Append(hash, "path:" + path);
      if (!File.Exists(path))
      {
        Append(hash, "missing");
        continue;
      }
      using var stream = File.OpenRead(path);
      Append(hash, Convert.ToHexString(SHA256.HashData(stream)));
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
  }

  private static async Task<string> ReadSharedTextAsync(string path, CancellationToken cancellationToken)
  {
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var reader = new StreamReader(stream);
    return await reader.ReadToEndAsync(cancellationToken);
  }

  private string BlobPath(string phase, string contentKey) => Path.Combine(_root, phase, "blobs", contentKey);

  private static void Append(IncrementalHash hash, string value)
  {
    var bytes = Encoding.UTF8.GetBytes(value);
    Span<byte> length = stackalloc byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
    hash.AppendData(length);
    hash.AppendData(bytes);
  }

  private async Task WriteAtomicAsync(string path, string contents, CancellationToken cancellationToken)
  {
    using var writeLock = await AcquireWriteLockAsync(path, cancellationToken);
    var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
      await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      File.Move(temporaryPath, path, true);
    }
    catch (UnauthorizedAccessException exception)
    {
      throw new IOException($"Unable to atomically replace cache file '{path}'.", exception);
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }
  }

  private async Task<FileStream> AcquireWriteLockAsync(string targetPath, CancellationToken cancellationToken)
  {
    var lockDirectory = Path.Combine(_root, ".locks");
    Directory.CreateDirectory(lockDirectory);
    var lockPath = Path.Combine(lockDirectory, CacheIdentity.Hash(Path.GetFullPath(targetPath).ToUpperInvariant()) + ".lock");
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var timeout = TimeSpan.FromSeconds(30);
    while (timer.Elapsed < timeout)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      }
      catch (IOException exception) when (IsLockContention(exception))
      {
        await Task.Delay(10, cancellationToken);
      }
    }

    throw new TimeoutException($"Timed out acquiring the artifact cache write lock for '{targetPath}'.");
  }

  private static bool IsLockContention(IOException exception)
  {
    var errorCode = exception.HResult & 0xFFFF;
    return OperatingSystem.IsWindows() ? errorCode is 32 or 33 : errorCode is 11 or 35;
  }

  private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
  {
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
    var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
    return Convert.ToHexString(hash).ToLowerInvariant();
  }
}
