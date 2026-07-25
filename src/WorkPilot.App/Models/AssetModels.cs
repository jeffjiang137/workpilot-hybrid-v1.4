namespace WorkPilot.Models;

public static class IndexPolicyV13
{
    public const int MaxFilesPerProject = 100_000;
    public const int MaxDepth = 32;
    public const int ScanPageSize = 200;
    public const int MaxIndexTextBytes = 524_288;
    public const int TargetChunkTokens = 800;
    public const int ChunkOverlapTokens = 100;
    public const int MaxChunkTokens = 1_200;
    public const int MaxChunksPerAsset = 2_000;
    public const int WriteBatchAssets = 100;
    public const int WriteBatchTextBytes = 5_242_880;
}

public static class SearchPolicyV13
{
    public const int DefaultLimit = 20;
    public const int HardLimit = 50;
    public const int CandidateLimit = 100;
    public const int MaxQueryTextElements = 200;
    public const int ContextMaxChunks = 8;
    public const int ContextMaxCharsPerChunk = 4_000;
    public const int ContextMaxCharsTotal = 20_000;
    public const int PreviewMaxBytes = 102_400;
    public const int PreviewMaxLines = 2_000;
}

public sealed record ScanItem(string RelativePath, string PathKey, string FileName, string Extension,
    long SizeBytes, long ModifiedUnixMs, uint Attributes);

public sealed record ScanPage(bool Done, bool Cancelled, bool LimitReached, int DirectoriesSeen,
    int FilesSeen, IReadOnlyList<ScanItem> Items);

public sealed record AssetRecord(long Id, string PublicId, string ProjectId, string ProjectName,
    string RelativePath, string FileName, string Extension, string Category, long SizeBytes,
    long ModifiedUnixMs, string? Sha256, string TextStatus, long Generation);

public sealed record TextChunk(int Ordinal, int StartOffset, int EndOffset, int TokenEstimate,
    string Content, string SearchText, string ContentHash);

public sealed record AssetSearchQuery(string SpaceId, string Query, string? ProjectId = null,
    string? Category = null, string? TextStatus = null, int? ModifiedWithinDays = null,
    int Limit = SearchPolicyV13.DefaultLimit, int Offset = 0);

public sealed record AssetSearchResult(long AssetId, long? ChunkId, string ProjectId, string ProjectName,
    string FileName, string RelativePath, string Category, string TextStatus, long SizeBytes,
    long ModifiedUnixMs, string Snippet, double Score);

public sealed record AssetPreview(long AssetId, string ProjectName, string FileName, string RelativePath,
    string TextStatus, long SizeBytes, long ModifiedUnixMs, string? Sha256, string Content, bool Truncated);

public sealed record IndexState(string ProjectId, string Status, long Generation, int DiscoveredCount,
    int ProcessedCount, int IndexedTextCount, int SkippedCount, int ErrorCount, string? CurrentPath,
    DateTimeOffset? LastFullScanAt, string? LastErrorMessage);
