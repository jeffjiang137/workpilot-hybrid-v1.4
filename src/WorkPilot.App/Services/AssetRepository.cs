using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class AssetRepository(DatabaseService database)
{
    public async Task<long> BeginGenerationAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO asset_index_state(project_id,status,generation,updated_at)
            VALUES($project,'discovering',1,$now)
            ON CONFLICT(project_id) DO UPDATE SET status='discovering',generation=generation+1,
              discovered_count=0,processed_count=0,indexed_text_count=0,skipped_count=0,error_count=0,
              current_path=NULL,last_error_code=NULL,last_error_message=NULL,updated_at=$now
            RETURNING generation
            """;
        command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<string?> GetFingerprintAsync(string projectId, string pathKey, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT quick_fingerprint FROM assets WHERE project_id=$project AND path_key=$path";
        command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$path", pathKey);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task TouchAsync(string projectId, string pathKey, long generation,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE assets SET generation=$generation,last_seen_at=$now WHERE project_id=$project AND path_key=$path AND EXISTS(SELECT 1 FROM asset_index_state s WHERE s.project_id=$project AND s.generation=$generation)";
        command.Parameters.AddWithValue("$generation", generation); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$path", pathKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateProgressAsync(string projectId, long generation, IndexCounters counters,
        string currentPath, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE asset_index_state SET status='scanning',discovered_count=$discovered,processed_count=$processed,indexed_text_count=$indexed,skipped_count=$skipped,error_count=$errors,current_path=$path,updated_at=$now WHERE project_id=$project AND generation=$generation";
        command.Parameters.AddWithValue("$discovered", counters.Discovered); command.Parameters.AddWithValue("$processed", counters.Processed);
        command.Parameters.AddWithValue("$indexed", counters.Indexed); command.Parameters.AddWithValue("$skipped", counters.Skipped);
        command.Parameters.AddWithValue("$errors", counters.Errors); command.Parameters.AddWithValue("$path", currentPath);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$generation", generation); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpsertAsync(Project project, ScanItem item, string fingerprint, string status,
        string? sha256, IReadOnlyList<TextChunk> chunks, long generation, bool replaceChunks,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var generationCommand = connection.CreateCommand(); generationCommand.Transaction = (SqliteTransaction)transaction;
        generationCommand.CommandText = "SELECT generation FROM asset_index_state WHERE project_id=$project";
        generationCommand.Parameters.AddWithValue("$project", project.Id);
        if (Convert.ToInt64(await generationCommand.ExecuteScalarAsync(cancellationToken)) != generation) return false;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var upsert = connection.CreateCommand(); upsert.Transaction = (SqliteTransaction)transaction;
        upsert.CommandText = """
            INSERT INTO assets(public_id,project_id,normalized_path,path_key,display_path,file_name,extension,category,size_bytes,modified_unix_ms,quick_fingerprint,sha256,text_status,generation,last_seen_at,created_at,updated_at)
            VALUES($public,$project,$path,$key,$path,$name,$extension,$category,$size,$modified,$fingerprint,$sha,$status,$generation,$now,$now,$now)
            ON CONFLICT(project_id,path_key) DO UPDATE SET normalized_path=$path,display_path=$path,file_name=$name,
              extension=$extension,category=$category,size_bytes=$size,modified_unix_ms=$modified,
              quick_fingerprint=$fingerprint,sha256=$sha,text_status=$status,generation=$generation,last_seen_at=$now,updated_at=$now
            RETURNING id
            """;
        BindAsset(upsert, project.Id, item, fingerprint, status, sha256, generation, now);
        var assetId = Convert.ToInt64(await upsert.ExecuteScalarAsync(cancellationToken));
        if (replaceChunks)
        {
            var delete = connection.CreateCommand(); delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM asset_chunks WHERE asset_id=$asset"; delete.Parameters.AddWithValue("$asset", assetId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            foreach (var chunk in chunks) await InsertChunkAsync(connection, (SqliteTransaction)transaction,
                assetId, item, chunk, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken); return true;
    }

    public async Task CompleteAsync(string projectId, long generation, bool limitReached, IndexCounters counters,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stale = connection.CreateCommand(); stale.Transaction = (SqliteTransaction)transaction;
        stale.CommandText = "DELETE FROM assets WHERE project_id=$project AND generation<$generation";
        stale.Parameters.AddWithValue("$project", projectId); stale.Parameters.AddWithValue("$generation", generation);
        await stale.ExecuteNonQueryAsync(cancellationToken);
        var state = connection.CreateCommand(); state.Transaction = (SqliteTransaction)transaction;
        state.CommandText = """
            UPDATE asset_index_state SET status=$status,discovered_count=$discovered,processed_count=$processed,
              indexed_text_count=$indexed,skipped_count=$skipped,error_count=$errors,current_path=NULL,
              last_full_scan_at=$now,updated_at=$now WHERE project_id=$project AND generation=$generation
            """;
        state.Parameters.AddWithValue("$status", limitReached ? "limit_reached" : "ready");
        state.Parameters.AddWithValue("$discovered", counters.Discovered); state.Parameters.AddWithValue("$processed", counters.Processed);
        state.Parameters.AddWithValue("$indexed", counters.Indexed); state.Parameters.AddWithValue("$skipped", counters.Skipped);
        state.Parameters.AddWithValue("$errors", counters.Errors); state.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        state.Parameters.AddWithValue("$project", projectId); state.Parameters.AddWithValue("$generation", generation);
        await state.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public async Task PauseAsync(string projectId, string? error, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE asset_index_state SET status=$status,last_error_message=$error,updated_at=$now WHERE project_id=$project";
        command.Parameters.AddWithValue("$status", error is null ? "paused" : "error");
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$project", projectId); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IndexState?> GetStateAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id,status,generation,discovered_count,processed_count,indexed_text_count,skipped_count,error_count,current_path,last_full_scan_at,last_error_message FROM asset_index_state WHERE project_id=$project";
        command.Parameters.AddWithValue("$project", projectId); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10)) : null;
    }

    public async Task<string> GetGenerationSummaryAsync(string spaceId, string? projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT p.id,COALESCE(s.generation,0) FROM projects p LEFT JOIN asset_index_state s ON s.project_id=p.id WHERE p.space_id=$space AND ($project IS NULL OR p.id=$project) ORDER BY p.id";
        command.Parameters.AddWithValue("$space", spaceId); command.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
        var values = new List<string>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0) + ":" + reader.GetInt64(1));
        return string.Join('|', values);
    }

    private static void BindAsset(SqliteCommand command, string projectId, ScanItem item, string fingerprint,
        string status, string? sha256, long generation, string now)
    {
        command.Parameters.AddWithValue("$public", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$path", item.RelativePath); command.Parameters.AddWithValue("$key", item.PathKey);
        command.Parameters.AddWithValue("$name", item.FileName); command.Parameters.AddWithValue("$extension", item.Extension.ToLowerInvariant());
        command.Parameters.AddWithValue("$category", AssetTypePolicy.Category(item.FileName, item.Extension)); command.Parameters.AddWithValue("$size", item.SizeBytes);
        command.Parameters.AddWithValue("$modified", item.ModifiedUnixMs); command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$sha", (object?)sha256 ?? DBNull.Value); command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$generation", generation); command.Parameters.AddWithValue("$now", now);
    }

    private static async Task InsertChunkAsync(SqliteConnection connection, SqliteTransaction transaction, long assetId,
        ScanItem item, TextChunk chunk, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO asset_chunks(asset_id,ordinal,start_offset,end_offset,token_estimate,content,search_text,file_name_tokens,path_tokens,content_hash) VALUES($asset,$ordinal,$start,$end,$tokens,$content,$search,$name,$path,$hash)";
        command.Parameters.AddWithValue("$asset", assetId); command.Parameters.AddWithValue("$ordinal", chunk.Ordinal);
        command.Parameters.AddWithValue("$start", chunk.StartOffset); command.Parameters.AddWithValue("$end", chunk.EndOffset);
        command.Parameters.AddWithValue("$tokens", chunk.TokenEstimate); command.Parameters.AddWithValue("$content", chunk.Content);
        command.Parameters.AddWithValue("$search", chunk.SearchText); command.Parameters.AddWithValue("$name", SearchTextNormalizer.ExpandForFts(item.FileName, true));
        command.Parameters.AddWithValue("$path", SearchTextNormalizer.ExpandForFts(item.RelativePath, true)); command.Parameters.AddWithValue("$hash", chunk.ContentHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record IndexCounters(int Discovered, int Processed, int Indexed, int Skipped, int Errors);

public static class AssetTypePolicy
{
    private static readonly HashSet<string> Code = [".js", ".jsx", ".ts", ".tsx", ".cs", ".cpp", ".cc", ".c", ".h", ".hpp", ".java", ".kt", ".py", ".go", ".rs", ".php", ".rb", ".swift", ".ps1", ".bat", ".cmd", ".css", ".scss", ".less"];
    private static readonly HashSet<string> Document = [".txt", ".md", ".markdown", ".html", ".htm"];
    private static readonly HashSet<string> Data = [".json", ".jsonc", ".xml", ".yaml", ".yml", ".csv", ".tsv", ".sql"];
    private static readonly HashSet<string> Config = [".ini", ".toml", ".env", ".gitignore"];
    public static string Category(string fileName, string extension)
    {
        var value = extension.ToLowerInvariant();
        if (Code.Contains(value)) return "code"; if (Document.Contains(value)) return "document";
        if (Data.Contains(value)) return "data"; if (Config.Contains(value) || fileName is "Dockerfile" or "Makefile") return "config";
        return "other";
    }
    public static bool SupportsText(string fileName, string extension) => Category(fileName, extension) != "other";
}
