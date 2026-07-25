using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WorkPilot.Application.Security.Retention;

/// <summary>
/// Persists a stable secret-scanning key (Windows DPAPI) and derives the production canary set from
/// it. Mirrors <see cref="SecretService"/>'s DPAPI envelope. The key and canaries are stable across
/// process runs, so a canary planted in any diagnostic log is reliably detected by the support-bundle
/// scan. WinUI-only (DPAPI); not compiled on the platform-independent sandboxed test runtime.
/// </summary>
public sealed class SecretScanningKeyProvider
{
    private const int KeyBytes = 32;
    private const int CanaryCount = 3;
    private const string CanaryPrefix = "WP_CANY_";

    private readonly string _keyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WorkPilot", "secrets", "scanning.key");

    /// <summary>Returns the stable scanning key, generating and persisting one on first use.</summary>
    public byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var plain = Unprotect(File.ReadAllBytes(_keyPath));
            try { return Normalize(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        var key = new byte[KeyBytes];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        var temporary = _keyPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, Protect(key));
        File.Move(temporary, _keyPath, overwrite: true);
        return key;
    }

    /// <summary>Deterministically derives the production canary set from the scanning key.</summary>
    public ISet<string> DeriveCanaryTokens(byte[] key)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        using var hmac = new HMACSHA256(key);
        for (var i = 0; i < CanaryCount; i++)
        {
            var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes("workpilot-support-canary-v1-" + i));
            tokens.Add(CanaryPrefix + ToUrlSafeBase64(digest));
        }
        return tokens;
    }

    /// <summary>Builds a <see cref="SecretScanningProfile"/> carrying the derived canaries.</summary>
    public SecretScanningProfile BuildProfile(byte[] key) => new(key, DeriveCanaryTokens(key));

    private static byte[] Normalize(byte[] key)
    {
        if (key.Length == KeyBytes) return key;
        // Defensive: a persisted key of unexpected length is re-derived deterministically to 32 bytes.
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes("workpilot-scanning-key"));
    }

    private static string ToUrlSafeBase64(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Protect(byte[] input) => Transform(input, true);
    private static byte[] Unprotect(byte[] input) => Transform(input, false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();
        try
        {
            inputBlob.Size = input.Length;
            inputBlob.Data = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);
            var ok = protect
                ? CryptProtectData(ref inputBlob, "WorkPilot scanning key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob);
            if (!ok) throw new InvalidOperationException("Windows DPAPI 操作失败", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(inputBlob.Data);
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
