using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WorkPilot.Services;

public sealed class SecretService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkPilot", "credential.bin");
    private readonly string _credentialRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkPilot", "secrets");

    public bool HasApiKey => File.Exists(_path);

    public void SaveApiKey(string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var plain = Encoding.UTF8.GetBytes(apiKey);
        try { File.WriteAllBytes(_path, Protect(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public string? LoadApiKey()
    {
        if (!File.Exists(_path)) return null;
        var encrypted = File.ReadAllBytes(_path);
        var plain = Unprotect(encrypted);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void DeleteApiKey()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    public void SaveCredential(string credentialRef, IReadOnlyDictionary<string, string> fields)
    {
        ValidateReference(credentialRef); Directory.CreateDirectory(_credentialRoot);
        var plain = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(fields));
        try
        {
            if (plain.Length > 64 * 1024) throw new ArgumentException("凭据载荷超过 64 KiB");
            var destination = GetCredentialPath(credentialRef); var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, Protect(plain)); File.Move(temporary, destination, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public SecretLease OpenCredential(string credentialRef)
    {
        ValidateReference(credentialRef); var path = GetCredentialPath(credentialRef);
        if (!File.Exists(path)) throw new InvalidOperationException("连接凭据不存在，请重新连接");
        var plain = Unprotect(File.ReadAllBytes(path));
        try
        {
            var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(plain)
                ?? throw new InvalidDataException("连接凭据格式无效");
            return new SecretLease(fields, plain);
        }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }

    public void DeleteCredential(string credentialRef)
    {
        ValidateReference(credentialRef); var path = GetCredentialPath(credentialRef);
        if (File.Exists(path)) File.Delete(path);
    }

    private string GetCredentialPath(string credentialRef) => Path.Combine(_credentialRoot, credentialRef + ".bin");

    private static void ValidateReference(string credentialRef)
    {
        if (credentialRef.Length != 32 || !Guid.TryParseExact(credentialRef, "N", out _))
            throw new ArgumentException("credential_ref 无效");
    }

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
                ? CryptProtectData(ref inputBlob, "WorkPilot API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref outputBlob)
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

public sealed class SecretLease : IDisposable
{
    private Dictionary<string, string>? _fields;
    private byte[]? _plain;
    internal SecretLease(Dictionary<string, string> fields, byte[] plain) { _fields = fields; _plain = plain; }
    public string GetRequired(string name) => _fields is not null && _fields.TryGetValue(name, out var value)
        ? value : throw new InvalidOperationException($"凭据字段缺失：{name}");
    public void Dispose()
    {
        if (_plain is not null) CryptographicOperations.ZeroMemory(_plain);
        if (_fields is not null) foreach (var key in _fields.Keys.ToList()) _fields[key] = string.Empty;
        _plain = null; _fields = null;
    }
}
