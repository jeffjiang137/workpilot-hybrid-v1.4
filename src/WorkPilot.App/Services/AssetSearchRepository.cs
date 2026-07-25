using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class AssetSearchRepository(DatabaseService database)
{
    public async Task<IReadOnlyList<AssetSearchResult>> SearchAsync(AssetSearchQuery query, string match,
        CancellationToken cancellationToken)
    {
        var results = new List<AssetSearchResult>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        if (match.Length > 0) await ReadFtsAsync(connection, query, match, results, cancellationToken);
        await ReadMetadataAsync(connection, query, results, cancellationToken);
        return results.GroupBy(x => x.AssetId).Select(group => group.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.ModifiedUnixMs)
            .ThenBy(x => x.ProjectId, StringComparer.Ordinal).ThenBy(x => x.RelativePath, StringComparer.Ordinal)
            .Skip(query.Offset).Take(Math.Clamp(query.Limit, 1, SearchPolicyV13.HardLimit)).ToArray();
    }

    public async Task<AssetRecord?> GetAssetAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = """
            SELECT a.id,a.public_id,a.project_id,p.name,a.display_path,a.file_name,a.extension,a.category,
              a.size_bytes,a.modified_unix_ms,a.sha256,a.text_status,a.generation
            FROM assets a JOIN projects p ON p.id=a.project_id WHERE a.id=$id
            """;
        command.Parameters.AddWithValue("$id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAsset(reader) : null;
    }

    public async Task<IReadOnlyList<(int Ordinal, string Content)>> GetChunksAsync(long assetId, int limit,
        CancellationToken cancellationToken)
    {
        var result = new List<(int, string)>();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT ordinal,content FROM asset_chunks WHERE asset_id=$asset ORDER BY ordinal LIMIT $limit";
        command.Parameters.AddWithValue("$asset", assetId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 8));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add((reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    private static async Task ReadFtsAsync(SqliteConnection connection, AssetSearchQuery query, string match,
        ICollection<AssetSearchResult> output, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand(); command.CommandText = """
            SELECT a.id,c.id,a.project_id,p.name,a.file_name,a.display_path,a.category,a.text_status,a.size_bytes,
              a.modified_unix_ms,c.content,-bm25(asset_chunks_fts,1.0,4.0,2.0)
            FROM asset_chunks_fts JOIN asset_chunks c ON c.id=asset_chunks_fts.rowid
            JOIN assets a ON a.id=c.asset_id JOIN projects p ON p.id=a.project_id
            WHERE asset_chunks_fts MATCH $match AND p.space_id=$space
              AND ($project IS NULL OR a.project_id=$project) AND ($category IS NULL OR a.category=$category)
              AND ($status IS NULL OR a.text_status=$status) AND ($since IS NULL OR a.modified_unix_ms>=$since)
            ORDER BY bm25(asset_chunks_fts,1.0,4.0,2.0) LIMIT 100
            """;
        BindFilters(command, query); command.Parameters.AddWithValue("$match", match);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) output.Add(MapResult(reader));
    }

    private static async Task ReadMetadataAsync(SqliteConnection connection, AssetSearchQuery query,
        ICollection<AssetSearchResult> output, CancellationToken cancellationToken)
    {
        var normalized = SearchTextNormalizer.Normalize(query.Query.Trim());
        var command = connection.CreateCommand(); command.CommandText = """
            SELECT a.id,NULL,a.project_id,p.name,a.file_name,a.display_path,a.category,a.text_status,a.size_bytes,
              a.modified_unix_ms,'',CASE WHEN $query='' THEN 1.0 WHEN lower(a.file_name)=lower($query) THEN 5.0 ELSE 2.0 END
            FROM assets a JOIN projects p ON p.id=a.project_id WHERE p.space_id=$space
              AND ($query='' OR lower(a.file_name) LIKE $like OR lower(a.display_path) LIKE $like)
              AND ($project IS NULL OR a.project_id=$project) AND ($category IS NULL OR a.category=$category)
              AND ($status IS NULL OR a.text_status=$status) AND ($since IS NULL OR a.modified_unix_ms>=$since)
            ORDER BY a.modified_unix_ms DESC LIMIT 100
            """;
        BindFilters(command, query); command.Parameters.AddWithValue("$query", normalized);
        command.Parameters.AddWithValue("$like", "%" + normalized.Replace("%", "[%]").Replace("_", "[_]") + "%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) output.Add(MapResult(reader));
    }

    private static void BindFilters(SqliteCommand command, AssetSearchQuery query)
    {
        command.Parameters.AddWithValue("$space", query.SpaceId);
        command.Parameters.AddWithValue("$project", (object?)query.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)query.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)query.TextStatus ?? DBNull.Value);
        long? since = query.ModifiedWithinDays is null ? null :
            DateTimeOffset.UtcNow.AddDays(-query.ModifiedWithinDays.Value).ToUnixTimeMilliseconds();
        command.Parameters.AddWithValue("$since", (object?)since ?? DBNull.Value);
    }

    private static AssetSearchResult MapResult(SqliteDataReader r) => new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1),
        r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetInt64(8), r.GetInt64(9), Limit(r.GetString(10), 360), r.GetDouble(11));
    private static AssetRecord MapAsset(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8),
        r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11), r.GetInt64(12));
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}
