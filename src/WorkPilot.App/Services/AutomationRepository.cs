using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class AutomationRepository
{
    private readonly string _databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkPilot", "workpilot.db");

    public async Task<IReadOnlyList<Automation>> GetAllAsync()
    {
        var items = new List<Automation>();
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,prompt,interval_minutes,enabled,last_run_at,next_run_at,last_status FROM automations ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetBoolean(4), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            DateTimeOffset.Parse(reader.GetString(6)), reader.GetString(7)));
        return items;
    }

    public async Task SaveAsync(Automation automation)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
          INSERT INTO automations(id,name,prompt,interval_minutes,enabled,last_run_at,next_run_at,last_status)
          VALUES($id,$name,$prompt,$interval,$enabled,$last,$next,$status)
          ON CONFLICT(id) DO UPDATE SET name=$name,prompt=$prompt,interval_minutes=$interval,enabled=$enabled,last_run_at=$last,next_run_at=$next,last_status=$status
          """;
        command.Parameters.AddWithValue("$id", automation.Id); command.Parameters.AddWithValue("$name", automation.Name);
        command.Parameters.AddWithValue("$prompt", automation.Prompt); command.Parameters.AddWithValue("$interval", automation.IntervalMinutes);
        command.Parameters.AddWithValue("$enabled", automation.Enabled); command.Parameters.AddWithValue("$last", (object?)automation.LastRunAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", automation.NextRunAt.ToString("O")); command.Parameters.AddWithValue("$status", automation.LastStatus);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string id)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = "DELETE FROM automations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        return connection;
    }
}

