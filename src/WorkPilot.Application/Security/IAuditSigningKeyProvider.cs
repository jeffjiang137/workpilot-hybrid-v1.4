namespace WorkPilot.Application.Security;

/// <summary>
/// Supplies the HMAC signing key for the security audit chain. In production this MUST resolve to a
/// platform secret (DPAPI / OS keychain) so the chain is tamper-evident against anyone with only DB
/// access. A constant key (see <see cref="StaticAuditKeyProvider"/>) is acceptable only as a fallback
/// or in tests — it detects accidental corruption, not a determined attacker holding the binary.
/// </summary>
public interface IAuditSigningKeyProvider
{
    byte[] GetKey();
}

/// <summary>
/// Default key provider using a fixed 32-byte application key. Documented as tamper-EVIDENT only;
/// replace with a platform-backed secret in shipped builds (T23 / installer key provisioning).
/// </summary>
public sealed class StaticAuditKeyProvider : IAuditSigningKeyProvider
{
    // NOT a real secret — placeholder so the chain detectably breaks on accidental corruption.
    // Override via DI with a platform key provider before any shipped build.
    private static readonly byte[] Key =
    {
        0x77, 0x6f, 0x72, 0x6b, 0x70, 0x69, 0x6c, 0x6f, 0x74, 0x2d, 0x61, 0x75, 0x64, 0x69, 0x74, 0x2d,
        0x68, 0x6d, 0x61, 0x63, 0x2d, 0x6b, 0x65, 0x79, 0x2d, 0x76, 0x31, 0x35, 0x2d, 0x73, 0x65, 0x63
    };

    public byte[] GetKey() => (byte[])Key.Clone();
}
