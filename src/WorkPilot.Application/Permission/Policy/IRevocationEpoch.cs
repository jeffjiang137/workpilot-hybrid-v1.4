namespace WorkPilot.Application.Permission.Policy;

/// <summary>
/// Process-wide revocation epoch source (doc 07 §11/§15/§17). Bumping the epoch invalidates every
/// previously-issued permit, consent receipt, and automation grant whose bound epoch differs, so a
/// policy change that widens (or restricts) access cannot be bypassed by stale credentials. Decoupled
/// from the policy store so the admin service can bump it without depending on the run/permit core
/// directly; the real host wires this to <c>ManagedPermitCore</c> / the native permit registry.
/// </summary>
public interface IRevocationEpoch
{
    /// <summary>Current epoch observed by permits/receipts/grants.</summary>
    long Current { get; }

    /// <summary>Increment the epoch. After this returns, any credential issued at the prior epoch fails
    /// its Current-State Check (doc 07 §11).</summary>
    void Bump();
}
