using Microsoft.Data.Sqlite;

using NUnit.Framework;

namespace LargeIntegrationSample.Tests;

[TestFixture]
public sealed class SqliteThresholdIntegrationTests
{
  private static IEnumerable<TestCaseData> Groups => Enumerable.Range(0, 100)
      .Select(group => new TestCaseData(group));

  [TestCaseSource(nameof(Groups))]
  public async Task SharedSqliteRowsDriveAllOperationsInTheGroup(int group)
  {
    var shard = group % 4;
    var root = Environment.GetEnvironmentVariable("MUTAGEN_LARGE_SQLITE_ROOT") ?? Path.Combine(Path.GetTempPath(), "MutagenLargeIntegration");
    Directory.CreateDirectory(root);
    var databasePath = Path.Combine(root, $"shard-{shard}.db");
    var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    await using (var createTable = connection.CreateCommand())
    {
      createTable.CommandText = "CREATE TABLE IF NOT EXISTS Thresholds (Id INTEGER PRIMARY KEY, Value INTEGER NOT NULL)";
      await createTable.ExecuteNonQueryAsync();
    }

    for (var operation = group * 10; operation < (group + 1) * 10; operation++)
    {
      await using (var upsert = connection.CreateCommand())
      {
        upsert.CommandText = "INSERT INTO Thresholds (Id, Value) VALUES ($id, $value) ON CONFLICT(Id) DO UPDATE SET Value = excluded.Value";
        upsert.Parameters.AddWithValue("$id", operation);
        upsert.Parameters.AddWithValue("$value", operation);
        await upsert.ExecuteNonQueryAsync();
      }

      int storedThreshold;
      await using (var select = connection.CreateCommand())
      {
        select.CommandText = "SELECT Value FROM Thresholds WHERE Id = $id";
        select.Parameters.AddWithValue("$id", operation);
        storedThreshold = Convert.ToInt32(await select.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
      }

      var method = typeof(ThresholdOperations).GetMethod($"Evaluate{operation:D3}")!;
      var atThreshold = (bool)method.Invoke(null, [storedThreshold])!;
      var aboveThreshold = (bool)method.Invoke(null, [storedThreshold + 1])!;
      Assert.That(atThreshold, Is.False, $"Operation {operation} should reject its stored threshold.");
      Assert.That(aboveThreshold, Is.True, $"Operation {operation} should accept the value above its stored threshold.");
    }
  }
}

