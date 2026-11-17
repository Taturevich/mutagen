namespace MutationFramework.Sdk;

public static class MutationEngine
{
  public static async Task<MutationRunResult> RunAsync(MutationOptions options, CancellationToken cancellationToken = default) => await Engine.RunAsync(options, true, cancellationToken);
  public static async Task<MutationRunResult> PlanAsync(MutationOptions options, CancellationToken cancellationToken = default) => await Engine.RunAsync(options, false, cancellationToken);
  public static async Task<MutationDiscoveryResult> DiscoverAsync(MutationOptions options, CancellationToken cancellationToken = default) => await MutationDiscoverySession.DiscoverAsync(options, cancellationToken);
}
