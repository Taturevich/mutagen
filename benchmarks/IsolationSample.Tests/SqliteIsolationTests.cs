using Microsoft.Data.Sqlite;

using NUnit.Framework;

namespace IsolationSample.Tests;

[TestFixture]
public sealed class MutantBailoutTests
{
  [Test]
  [Obsolete]
  public void BoundaryComparisonKillsEveryRelationalMutant()
  {
    Assert.That(IsolationOperation.IsAbove(10, 10), Is.False);
    Assert.That(IsolationOperation.IsAbove(10, 11), Is.True);
    Assert.That(IsolationOperation.IsAbove(11, 10), Is.False);
  }

  [Test, DependsOnTest(nameof(BoundaryComparisonKillsEveryRelationalMutant))]
  [Obsolete]
  public void LaterCoveredCaseWouldPassAndWritesTheMarker()
  {
    var marker = Environment.GetEnvironmentVariable("MUTAGEN_PER_MUTANT_BAIL_MARKER");
    if (!string.IsNullOrWhiteSpace(marker) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTATION_ACTIVE_ID")))
    {
      File.AppendAllText(marker, "executed" + Environment.NewLine);
    }

    Assert.That(IsolationOperation.IsAbove(10, 11), Is.True);
  }
}

[TestFixture]
public sealed class SqliteIsolationTests
{
  [TestCase(0)]
  [TestCase(1)]
  public async Task SqliteShardExercisesTheSharedMutationSite(int shard)
  {
    var root = Environment.GetEnvironmentVariable("MUTAGEN_ISOLATION_SQLITE_ROOT") ??
        Path.Combine(Path.GetTempPath(), "MutagenIsolationSample");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, $"shard-{shard}.db");
    var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    await using (var create = connection.CreateCommand())
    {
      create.CommandText = "CREATE TABLE IF NOT EXISTS Thresholds (Value INTEGER NOT NULL)";
      await create.ExecuteNonQueryAsync();
    }

    await using (var clear = connection.CreateCommand())
    {
      clear.CommandText = "DELETE FROM Thresholds";
      await clear.ExecuteNonQueryAsync();
    }

    await using (var insert = connection.CreateCommand())
    {
      insert.CommandText = "INSERT INTO Thresholds (Value) VALUES ($value)";
      insert.Parameters.AddWithValue("$value", shard + 10);
      await insert.ExecuteNonQueryAsync();
    }

    await using var select = connection.CreateCommand();
    select.CommandText = "SELECT Value FROM Thresholds";
    var threshold = Convert.ToInt32(await select.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    Assert.That(IsolationOperation.IsAbove(threshold, threshold + 1), Is.True);
  }
}
