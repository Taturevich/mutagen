namespace MutationFramework.Sdk;

internal sealed class BaselineFailureException(string message, IReadOnlyList<string> errors) : Exception(message)
{
  public IReadOnlyList<string> Errors { get; } = errors;
  public override string ToString() => base.ToString() + Environment.NewLine + string.Join(Environment.NewLine, Errors);
}
internal sealed class CoverageCollectionException(CoverageStatus status, string message, Exception? inner = null) : Exception(message, inner)
{
  public CoverageStatus Status { get; } = status;
}
