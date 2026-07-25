using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkPilot.Models;

namespace WorkPilot.Services;

public sealed class NativeWorkspaceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public static NativeWorkspaceException FromMessage(string message)
    {
        var separator = message.IndexOf(':');
        return separator is > 0 and < 40
            ? new(message[..separator], message[(separator + 1)..].Trim())
            : new("NATIVE_ERROR", message);
    }
}

public interface INativeWorkspaceFactory
{
    INativeWorkspaceSession Open(string absoluteRoot);
    int EvaluatePermission(int mode, int risk, bool mutating);
}

public interface INativeWorkspaceSession : IDisposable
{
    string ListFiles(string relativePath = "", int maxItems = 200);
    string ReadText(string relativePath);
    string WriteText(string relativePath, string content, string? expectedSha256 = null);
    string QuickFingerprint(string relativePath);
    INativeScanSession BeginScan(bool includeHidden, string ignoreRules);
}

public interface INativeScanSession : IDisposable
{
    ScanPage Next(int maxItems = IndexPolicyV13.ScanPageSize);
    void Cancel();
}

public sealed class NativeWorkspaceFactory : INativeWorkspaceFactory
{
    public INativeWorkspaceSession Open(string absoluteRoot) => new NativeWorkspaceSession(absoluteRoot);
    public int EvaluatePermission(int mode, int risk, bool mutating) =>
        NativeMethods.wp_evaluate_permission(mode, risk, mutating ? 1 : 0);
}

internal sealed class NativeWorkspaceSession : INativeWorkspaceSession
{
    private IntPtr _context;

    public NativeWorkspaceSession(string absoluteRoot)
    {
        if (NativeMethods.wp_abi_version() != 0x00010300)
            throw new InvalidOperationException("C++ 核心 ABI 版本不匹配，请重新构建完整安装包");
        _context = NativeMethods.wp_create();
        if (_context == IntPtr.Zero) throw new InvalidOperationException("无法初始化 C++ 核心");
        if (NativeMethods.wp_set_workspace(_context, absoluteRoot) != 0)
        {
            var error = GetLastError(); Dispose(); throw NativeWorkspaceException.FromMessage(error);
        }
    }

    public string ListFiles(string relativePath = "", int maxItems = 200) =>
        ReadNative(NativeMethods.wp_list_files(_context, relativePath, maxItems));
    public string ReadText(string relativePath) => ReadNative(NativeMethods.wp_read_text(_context, relativePath));
    public string QuickFingerprint(string relativePath) =>
        ReadNative(NativeMethods.wp_quick_fingerprint(_context, relativePath));

    public string WriteText(string relativePath, string content, string? expectedSha256 = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content + "\0");
        var hash = expectedSha256 is null ? null : Encoding.UTF8.GetBytes(expectedSha256 + "\0");
        return ReadNative(NativeMethods.wp_write_text(_context, relativePath, bytes, hash));
    }

    public INativeScanSession BeginScan(bool includeHidden, string ignoreRules)
    {
        var rules = ignoreRules.ReplaceLineEndings("\n").Split('\n')
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
        if (rules.Length > 200 || rules.Any(x => x.Length > 500))
            throw new ArgumentException("忽略规则最多 200 条，每条最多 500 个字符");
        var options = JsonSerializer.Serialize(new
        {
            version = 1, include_hidden = includeHidden, max_depth = IndexPolicyV13.MaxDepth,
            max_files = IndexPolicyV13.MaxFilesPerProject, ignore_rules = rules
        });
        var scan = NativeMethods.wp_scan_begin(_context, options);
        return scan == IntPtr.Zero ? throw new InvalidOperationException(GetLastError()) :
            new NativeScanSession(scan, this);
    }

    internal string ReadNative(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) throw NativeWorkspaceException.FromMessage(GetLastError());
        try { return Marshal.PtrToStringUTF8(pointer) ?? ""; }
        finally { NativeMethods.wp_free(pointer); }
    }

    private string GetLastError()
    {
        var pointer = NativeMethods.wp_last_error(_context);
        if (pointer == IntPtr.Zero) return "C++ 核心返回未知错误";
        try { return Marshal.PtrToStringUTF8(pointer) ?? "C++ 核心返回未知错误"; }
        finally { NativeMethods.wp_free(pointer); }
    }

    public void Dispose()
    {
        if (_context == IntPtr.Zero) return;
        NativeMethods.wp_destroy(_context); _context = IntPtr.Zero; GC.SuppressFinalize(this);
    }
}

internal sealed class NativeScanSession(IntPtr scan, NativeWorkspaceSession owner) : INativeScanSession
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private IntPtr _scan = scan;

    public ScanPage Next(int maxItems = IndexPolicyV13.ScanPageSize)
    {
        if (_scan == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeScanSession));
        var json = owner.ReadNative(NativeMethods.wp_scan_next(_scan, Math.Clamp(maxItems, 1, IndexPolicyV13.ScanPageSize)));
        var page = JsonSerializer.Deserialize<ScanPageDto>(json, Options) ?? throw new InvalidDataException("扫描页 JSON 为空");
        return new(page.Done, page.Cancelled, page.LimitReached, page.DirectoriesSeen, page.FilesSeen,
            page.Items.Select(x => new ScanItem(x.RelativePath, x.PathKey, x.FileName, x.Extension,
                x.SizeBytes, x.ModifiedUnixMs, x.Attributes)).ToArray());
    }

    public void Cancel() { if (_scan != IntPtr.Zero) NativeMethods.wp_scan_cancel(_scan); }
    public void Dispose() { if (_scan == IntPtr.Zero) return; NativeMethods.wp_scan_destroy(_scan); _scan = IntPtr.Zero; }

    private sealed record ScanPageDto(
        [property: JsonPropertyName("done")] bool Done,
        [property: JsonPropertyName("cancelled")] bool Cancelled,
        [property: JsonPropertyName("limit_reached")] bool LimitReached,
        [property: JsonPropertyName("directories_seen")] int DirectoriesSeen,
        [property: JsonPropertyName("files_seen")] int FilesSeen,
        [property: JsonPropertyName("items")] IReadOnlyList<ScanItemDto> Items);
    private sealed record ScanItemDto(
        [property: JsonPropertyName("relative_path")] string RelativePath,
        [property: JsonPropertyName("path_key")] string PathKey,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("extension")] string Extension,
        [property: JsonPropertyName("size_bytes")] long SizeBytes,
        [property: JsonPropertyName("modified_unix_ms")] long ModifiedUnixMs,
        [property: JsonPropertyName("attributes")] uint Attributes);
}

internal static class NativeMethods
{
    private const string Dll = "workpilot_core.dll";
    private const CallingConvention Convention = CallingConvention.Cdecl;
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern int wp_abi_version();
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern IntPtr wp_create();
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern void wp_destroy(IntPtr context);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern int wp_set_workspace(IntPtr context, string path);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern int wp_evaluate_permission(int mode, int risk, int mutating);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern IntPtr wp_list_files(IntPtr context, string relativePath, int maxItems);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern IntPtr wp_read_text(IntPtr context, string relativePath);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern IntPtr wp_write_text(IntPtr context, string relativePath, byte[] content, byte[]? hash);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern IntPtr wp_scan_begin(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string options);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern IntPtr wp_scan_next(IntPtr scan, int maxItems);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern void wp_scan_cancel(IntPtr scan);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern void wp_scan_destroy(IntPtr scan);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true, CharSet = CharSet.Unicode)] internal static extern IntPtr wp_quick_fingerprint(IntPtr context, string relativePath);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern IntPtr wp_last_error(IntPtr context);
    [DllImport(Dll, CallingConvention = Convention, ExactSpelling = true)] internal static extern void wp_free(IntPtr value);
}
