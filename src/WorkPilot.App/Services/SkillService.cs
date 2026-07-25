using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed partial class SkillService
{
    private const long MaxArchiveBytes = 20 * 1024 * 1024;
    private const long MaxUncompressedBytes = 50 * 1024 * 1024;
    private const int MaxFiles = 200;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".txt", ".json", ".png", ".jpg", ".jpeg", ".webp" };
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DatabaseService _database;
    private readonly string _skillRoot;
    private readonly string _stagingRoot;
    private readonly Dictionary<string, PendingInspection> _pending = [];
    private readonly object _gate = new();

    public SkillService(DatabaseService database, string? root = null)
    {
        _database = database;
        var appRoot = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkPilot");
        _skillRoot = Path.Combine(appRoot, "skills"); _stagingRoot = Path.Combine(appRoot, "skill-staging");
        Directory.CreateDirectory(_skillRoot); Directory.CreateDirectory(_stagingRoot); CleanupStaging();
    }

    public async Task<IReadOnlyList<Skill>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<Skill>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,s.publisher,s.display_name,v.semantic_version,v.description,s.status,
                   v.package_sha256,v.content_root,s.installed_at_utc,
                   (SELECT COUNT(*) FROM expert_skills es WHERE es.skill_version_id=v.id AND es.enabled=1)
            FROM skills s JOIN skill_versions v ON v.id=s.active_version_id
            ORDER BY s.display_name COLLATE NOCASE LIMIT 200
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8)), reader.GetInt32(9)));
        return result;
    }

    public async Task<IReadOnlyList<(string VersionId, Skill Skill)>> GetEnabledVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<(string, Skill)>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id,s.id,s.publisher,s.display_name,v.semantic_version,v.description,s.status,
                   v.package_sha256,v.content_root,s.installed_at_utc
            FROM skills s JOIN skill_versions v ON v.id=s.active_version_id WHERE s.status='enabled'
            ORDER BY s.display_name COLLATE NOCASE LIMIT 100
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add((reader.GetString(0), new Skill(reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.GetString(8), DateTimeOffset.Parse(reader.GetString(9)))));
        return result;
    }

    public async Task<SkillInspection> InspectAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(zipPath) || !string.Equals(Path.GetExtension(zipPath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择一个存在的 .zip 技能包");
        var archiveLength = new FileInfo(zipPath).Length;
        if (archiveLength is <= 0 or > MaxArchiveBytes) throw new InvalidDataException("技能包为空或超过 20 MiB");
        var token = Guid.NewGuid().ToString("N"); var staging = Path.Combine(_stagingRoot, token);
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<string>(); long total = 0;
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count is < 2 or > MaxFiles) throw new InvalidDataException("技能包文件数必须为 2–200");
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var relative = ValidateEntry(entry, files); total = checked(total + entry.Length);
                if (total > MaxUncompressedBytes) throw new InvalidDataException("技能包解压后超过 50 MiB");
                if (entry.Length > 2 * 1024 * 1024) throw new InvalidDataException($"文件超过 2 MiB：{relative}");
                if (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > 100)
                    throw new InvalidDataException($"文件压缩比异常：{relative}");
                var destination = Path.GetFullPath(Path.Combine(staging, relative));
                EnsureUnderRoot(staging, destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await CopyBoundedAsync(source, output, entry.Length, cancellationToken);
                files.Add(relative.Replace('\\', '/'));
            }
            if (!files.Contains("workpilot-skill.json", StringComparer.Ordinal) || !files.Contains("SKILL.md", StringComparer.Ordinal))
                throw new InvalidDataException("技能包根目录必须包含 workpilot-skill.json 和 SKILL.md");
            var manifest = await ReadManifestAsync(Path.Combine(staging, "workpilot-skill.json"), cancellationToken);
            if (!files.Contains(manifest.Entrypoint, StringComparer.Ordinal)) throw new InvalidDataException("manifest entrypoint 不存在");
            var packageHash = await HashFileAsync(zipPath, cancellationToken);
            var inspection = new SkillInspection(token, zipPath, manifest, packageHash, files.Count, total, files, []);
            lock (_gate) _pending[token] = new PendingInspection(inspection, staging, DateTimeOffset.UtcNow);
            return inspection;
        }
        catch { TryDelete(staging); throw; }
    }

    public async Task<Skill> InstallAsync(string token, CancellationToken cancellationToken = default)
    {
        PendingInspection pending;
        lock (_gate)
        {
            if (!_pending.Remove(token, out pending!)) throw new InvalidOperationException("技能检查结果已失效，请重新选择文件");
        }
        if (DateTimeOffset.UtcNow - pending.CreatedAt > TimeSpan.FromMinutes(30))
        {
            TryDelete(pending.StagingPath); throw new InvalidOperationException("技能检查结果已过期，请重新检查");
        }
        var item = pending.Inspection; var versionId = Guid.NewGuid().ToString("N");
        var relative = Path.Combine(item.Manifest.Id, item.Manifest.Version, item.PackageSha256[..12]);
        var destination = Path.Combine(_skillRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var conflict = connection.CreateCommand();
        conflict.CommandText = "SELECT package_sha256 FROM skill_versions WHERE skill_id=$id AND semantic_version=$version";
        conflict.Parameters.AddWithValue("$id", item.Manifest.Id); conflict.Parameters.AddWithValue("$version", item.Manifest.Version);
        var existingHash = await conflict.ExecuteScalarAsync(cancellationToken) as string;
        if (existingHash is not null)
        {
            TryDelete(pending.StagingPath);
            if (existingHash != item.PackageSha256) throw new InvalidOperationException("同一技能版本的内容哈希不同，已拒绝覆盖");
            return (await ListAsync(cancellationToken)).Single(x => x.Id == item.Manifest.Id);
        }

        if (Directory.Exists(destination)) TryDelete(destination);
        Directory.Move(pending.StagingPath, destination);
        try
        {
            var instructionHash = await HashFileAsync(Path.Combine(destination, item.Manifest.Entrypoint), cancellationToken);
            var now = DateTimeOffset.UtcNow.ToString("O");
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var skill = connection.CreateCommand(); skill.Transaction = (SqliteTransaction)transaction;
            skill.CommandText = """
                INSERT INTO skills(id,publisher,display_name,active_version_id,status,source_kind,installed_at_utc,updated_at_utc,row_version)
                VALUES($id,$publisher,$name,$version,'enabled','local',$now,$now,1)
                ON CONFLICT(id) DO UPDATE SET publisher=$publisher,display_name=$name,active_version_id=$version,
                  status='enabled',updated_at_utc=$now,row_version=row_version+1
                """;
            skill.Parameters.AddWithValue("$id", item.Manifest.Id); skill.Parameters.AddWithValue("$publisher", item.Manifest.Publisher);
            skill.Parameters.AddWithValue("$name", item.Manifest.Name); skill.Parameters.AddWithValue("$version", versionId);
            skill.Parameters.AddWithValue("$now", now); await skill.ExecuteNonQueryAsync(cancellationToken);
            var version = connection.CreateCommand(); version.Transaction = (SqliteTransaction)transaction;
            version.CommandText = """
                INSERT INTO skill_versions(id,skill_id,semantic_version,description,manifest_json,package_sha256,
                  content_root,instruction_sha256,validation_status,installed_at_utc)
                VALUES($vid,$id,$semver,$description,$manifest,$hash,$root,$instruction,'valid',$now)
                """;
            version.Parameters.AddWithValue("$vid", versionId); version.Parameters.AddWithValue("$id", item.Manifest.Id);
            version.Parameters.AddWithValue("$semver", item.Manifest.Version); version.Parameters.AddWithValue("$description", item.Manifest.Description);
            version.Parameters.AddWithValue("$manifest", JsonSerializer.Serialize(item.Manifest, JsonOptions));
            version.Parameters.AddWithValue("$hash", item.PackageSha256); version.Parameters.AddWithValue("$root", relative);
            version.Parameters.AddWithValue("$instruction", instructionHash); version.Parameters.AddWithValue("$now", now);
            await version.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return (await ListAsync(cancellationToken)).Single(x => x.Id == item.Manifest.Id);
        }
        catch { TryDelete(destination); throw; }
    }

    public async Task SetEnabledAsync(string skillId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE skills SET status=$status,updated_at_utc=$now,row_version=row_version+1 WHERE id=$id";
        command.Parameters.AddWithValue("$status", enabled ? "enabled" : "disabled");
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$id", skillId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UninstallAsync(string skillId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var roots = new List<string>(); var query = connection.CreateCommand();
        query.CommandText = "SELECT content_root FROM skill_versions WHERE skill_id=$id"; query.Parameters.AddWithValue("$id", skillId);
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) roots.Add(reader.GetString(0));
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var unbind = connection.CreateCommand(); unbind.Transaction = (SqliteTransaction)transaction;
        unbind.CommandText = "DELETE FROM expert_skills WHERE skill_version_id IN (SELECT id FROM skill_versions WHERE skill_id=$id)";
        unbind.Parameters.AddWithValue("$id", skillId); await unbind.ExecuteNonQueryAsync(cancellationToken);
        var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM skills WHERE id=$id";
        command.Parameters.AddWithValue("$id", skillId); await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var root in roots) { var path = Path.GetFullPath(Path.Combine(_skillRoot, root)); EnsureUnderRoot(_skillRoot, path); TryDelete(path); }
    }

    public async Task<string> ReadInstructionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT content_root,manifest_json FROM skill_versions WHERE id=$id AND validation_status='valid'";
        command.Parameters.AddWithValue("$id", versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException("技能版本不存在");
        var manifest = JsonSerializer.Deserialize<SkillManifest>(reader.GetString(1), JsonOptions) ?? throw new InvalidDataException("技能 manifest 无效");
        var root = Path.GetFullPath(Path.Combine(_skillRoot, reader.GetString(0))); EnsureUnderRoot(_skillRoot, root);
        var path = Path.GetFullPath(Path.Combine(root, manifest.Entrypoint)); EnsureUnderRoot(root, path);
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        if (text.Length > 32_000) throw new InvalidDataException("技能入口超过 32,000 字符");
        return text;
    }

    private static string ValidateEntry(ZipArchiveEntry entry, IReadOnlyCollection<string> existing)
    {
        var path = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(path) || path.Contains(':') || path.Split(Path.DirectorySeparatorChar).Any(x => x is "" or "." or ".."))
            throw new InvalidDataException($"技能包包含不安全路径：{entry.FullName}");
        if (path.Length > 240 || path.Split(Path.DirectorySeparatorChar).Length > 8)
            throw new InvalidDataException($"技能包路径过长或层级过深：{entry.FullName}");
        if (existing.Contains(path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"技能包包含大小写冲突路径：{entry.FullName}");
        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
            (entry.ExternalAttributes & 0x400) != 0) throw new InvalidDataException("技能包不允许链接或重解析点");
        foreach (var segment in path.Split(Path.DirectorySeparatorChar))
            if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(segment)) || segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidDataException($"技能包包含 Windows 保留路径：{entry.FullName}");
        if (!AllowedExtensions.Contains(Path.GetExtension(path))) throw new InvalidDataException($"技能包包含不允许的文件类型：{entry.FullName}");
        return path;
    }

    private static async Task<SkillManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        if (json.Length > 64 * 1024) throw new InvalidDataException("技能 manifest 超过 64 KiB");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var allowed = new HashSet<string>(["schemaVersion", "id", "name", "publisher", "version", "description", "entrypoint", "minWorkPilotVersion", "activation", "requiredCapabilities"]);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw new InvalidDataException($"技能 manifest 包含未知字段：/{property.Name}");
        var value = JsonSerializer.Deserialize<SkillManifest>(json, JsonOptions) ?? throw new InvalidDataException("技能 manifest 无法解析");
        if (value.SchemaVersion != 1 || !SkillIdRegex().IsMatch(value.Id) || !value.Id.Contains('.') ||
            value.Id.Contains("..", StringComparison.Ordinal) || !SemVerRegex().IsMatch(value.Version))
            throw new InvalidDataException("技能 ID、版本或 schemaVersion 无效");
        if (value.Name.Trim().Length is < 1 or > 80 || value.Publisher.Trim().Length is < 1 or > 80 || value.Description.Length > 400)
            throw new InvalidDataException("技能名称、发布者或描述长度无效");
        if (value.Entrypoint != "SKILL.md") throw new InvalidDataException("V1.4 技能入口必须为 SKILL.md");
        if ((value.Activation?.Aliases?.Count ?? 0) > 20 || (value.Activation?.Tags?.Count ?? 0) > 30 ||
            (value.RequiredCapabilities?.Count ?? 0) > 32) throw new InvalidDataException("技能激活或能力声明超过数量上限");
        return value;
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long declaredLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920]; long copied = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken); if (count == 0) break;
            copied += count; if (copied > declaredLength || copied > 2 * 1024 * 1024) throw new InvalidDataException("解压数据超过声明大小");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path); using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private void CleanupStaging()
    {
        foreach (var directory in Directory.GetDirectories(_stagingRoot))
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromHours(24)) TryDelete(directory);
    }

    private static void EnsureUnderRoot(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出受控技能目录");
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppLogger.Error("Skill staging cleanup failed", error); }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{2,79}$", RegexOptions.CultureInvariant)] private static partial Regex SkillIdRegex();
    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)] private static partial Regex SemVerRegex();
    private sealed record PendingInspection(SkillInspection Inspection, string StagingPath, DateTimeOffset CreatedAt);
}
