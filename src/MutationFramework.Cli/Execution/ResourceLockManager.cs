using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace MutationFramework.Sdk;

internal sealed class ResourceLockManager : IDisposable
{
  private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

  public async Task<T> ExecuteAsync<T>(IReadOnlyList<string> resourceKeys, Func<Task<T>> action, CancellationToken cancellationToken)
  {
    var acquired = new List<SemaphoreSlim>();
    try
    {
      foreach (var key in resourceKeys.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
      {
        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        acquired.Add(gate);
      }
      return await action();
    }
    finally
    {
      for (var index = acquired.Count - 1; index >= 0; index--)
      {
        acquired[index].Release();
      }
    }
  }

  // The session disposes the registry after all baseline and mutation workers have joined.
  public void Dispose()
  {
    foreach (var gate in _locks.Values)
    {
      gate.Dispose();
    }
    _locks.Clear();
  }
}
