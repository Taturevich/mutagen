namespace MutationFramework.Sdk;

internal static class MutationResults
{
  internal static MutationResult ToResult(Candidate c, MutationStatus s, IReadOnlyList<string> tests, IReadOnlyList<string> killing, long ms, string? error = null) => new(c.Id, c.SiteId, c.Operator, c.File, c.Line, c.Column, c.Original, c.Replacement, s, tests, killing, ms, error);
}
