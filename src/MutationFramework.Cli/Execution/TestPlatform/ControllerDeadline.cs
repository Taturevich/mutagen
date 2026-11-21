namespace MutationFramework.Sdk;

internal static class ControllerDeadline
{
  internal static void Run(Action action, TimeSpan timeout, CancellationToken cancellationToken = default, Action? terminate = null)
  {
    var operation = Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    _ = operation.ContinueWith(task => _ = task.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    try
    {
      operation.WaitAsync(timeout, cancellationToken).GetAwaiter().GetResult();
    }
    catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
    {
      terminate?.Invoke();
      throw;
    }
  }
}
