using System.Diagnostics;
using System.Globalization;

namespace MutationFramework.Sdk;

internal sealed class MutationProgressReporter : IAsyncDisposable
{
  private readonly object _gate = new();
  private readonly int _total;
  private readonly int _workers;
  private readonly int _reused;
  private readonly Dictionary<MutationStatus, int> _outcomes = [];
  private readonly Stopwatch _elapsed = Stopwatch.StartNew();
  private readonly Action<string> _write;
  private readonly CancellationTokenSource _stop = new();
  private readonly Task _heartbeat;
  private readonly bool _enabled;
  private int _finished;
  private int _active;
  private string _state = "interrupted";

  internal MutationProgressReporter(int total, int workers, IEnumerable<MutationResult> restored,
      TimeSpan interval, Action<string>? write = null)
  {
    _total = total;
    _workers = workers;
    _write = write ?? Console.Error.WriteLine;
    _enabled = interval > TimeSpan.Zero;
    foreach (var result in restored)
    {
      Record(result);
      _reused++;
    }
    if (_enabled)
    {
      Emit($"Mutagen: execution starting; {total} covered mutations; {_reused} reused; {workers} workers.");
      _heartbeat = ReportPeriodicallyAsync(interval);
    }
    else
    {
      _heartbeat = Task.CompletedTask;
    }
  }

  internal void Record(MutationResult result)
  {
    lock (_gate)
    {
      _finished++;
      _outcomes[result.Status] = _outcomes.GetValueOrDefault(result.Status) + 1;
    }
  }

  internal IDisposable StartWork()
  {
    lock (_gate)
    {
      _active++;
    }
    return new ActiveWork(this);
  }

  internal void Complete(string state) => _state = state;

  internal string Snapshot(TimeSpan elapsed, string state = "running")
  {
    lock (_gate)
    {
      var measured = _finished - _reused;
      var remaining = Math.Max(0, _total - _finished);
      var rate = elapsed.TotalSeconds > 0 ? measured / elapsed.TotalSeconds : 0;
      var eta = remaining == 0 ? "00:00:00"
          : measured >= 5 && elapsed.TotalSeconds >= 10 && rate > 0
              ? "~" + Duration(TimeSpan.FromSeconds(Math.Min(remaining / rate, TimeSpan.MaxValue.TotalSeconds / 2)))
              : "warming up";
      return FormattableString.Invariant($"Mutagen: progress ({state}) {_finished}/{_total} ({(_total == 0 ? 100 : 100d * _finished / _total):F1}%); remaining {remaining}; killed {_outcomes.GetValueOrDefault(MutationStatus.Killed)}; survived {_outcomes.GetValueOrDefault(MutationStatus.Survived)}; timeout {_outcomes.GetValueOrDefault(MutationStatus.Timeout)}; infrastructure {_outcomes.GetValueOrDefault(MutationStatus.InfrastructureError)}; inconclusive {_outcomes.GetValueOrDefault(MutationStatus.Inconclusive)}; reused {_reused}; active workers {_active}/{_workers}; elapsed {Duration(elapsed)}; rate {rate * 60:F1} mutants/min; ETA {eta} (approximate).");
    }
  }

  private static string Duration(TimeSpan elapsed) =>
      ((long)elapsed.TotalHours).ToString("00", CultureInfo.InvariantCulture) + elapsed.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);

  private async Task ReportPeriodicallyAsync(TimeSpan interval)
  {
    using var timer = new PeriodicTimer(interval);
    try
    {
      while (await timer.WaitForNextTickAsync(_stop.Token))
      {
        Emit(Snapshot(_elapsed.Elapsed));
      }
    }
    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
  }

  private void Emit(string message)
  {
    try
    {
      _write(message);
    }
    catch (Exception exception) when (exception is IOException or ObjectDisposedException)
    {
      // A closed diagnostic stream must not abort mutation execution or checkpointing.
    }
  }

  public async ValueTask DisposeAsync()
  {
    await _stop.CancelAsync();
    await _heartbeat;
    if (_enabled)
    {
      Emit(Snapshot(_elapsed.Elapsed, _state));
    }
    _stop.Dispose();
  }

  private sealed class ActiveWork(MutationProgressReporter owner) : IDisposable
  {
    private MutationProgressReporter? _owner = owner;
    public void Dispose()
    {
      var current = Interlocked.Exchange(ref _owner, null);
      if (current is not null)
      {
        lock (current._gate)
        {
          current._active--;
        }
      }
    }
  }
}
