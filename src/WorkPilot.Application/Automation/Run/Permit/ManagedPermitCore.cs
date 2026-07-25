using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using WorkPilot.Application.Permission.Policy;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Automation.Run.Permit;

/// <summary>
/// Process-internal faithful model of the Native Permit registry. Holds an in-process signing key so a
/// permit handle cannot be forged by C#, and enforces: single-use, 30-second expiry, revocation-epoch
/// match, worker-lease ownership/expiry, and cancellation. This is the sandbox stand-in for the C++
/// Core ABI; the Host later supplies a <c>NativePermitCore</c> doing the real P/Invoke.
/// </summary>
public sealed class ManagedPermitCore : INativePermitCore, IRevocationEpoch
{
    private sealed class Entry
    {
        public PermitBinding Binding;
        public string Signature;
        public bool Consumed;
        public DateTimeOffset IssuedAt;

        public Entry(PermitBinding binding, string signature, bool consumed, DateTimeOffset issuedAt)
        {
            Binding = binding;
            Signature = signature;
            Consumed = consumed;
            IssuedAt = issuedAt;
        }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly IClock? _clock;
    private long _epoch;

    /// <summary>Optional clock used for the expiry/lease checks. Defaults to the real wall clock so the
    /// sandbox double matches the Native Core's OS-clock semantics; tests inject a deterministic
    /// <see cref="IClock"/> and must share it with the <see cref="PermitIssuer"/> so mint-time and
    /// check-time agree (otherwise permits expire instantly under a fake clock).</summary>
    public ManagedPermitCore(IClock? clock = null) => _clock = clock;

    public long CurrentRevocationEpoch
    {
        get => Volatile.Read(ref _epoch);
        set => Volatile.Write(ref _epoch, value);
    }

    private DateTimeOffset Now => _clock?.UtcNow ?? DateTimeOffset.UtcNow;

    public IExecutionPermit Issue(PermitBinding binding)
    {
        var permitId = Guid.NewGuid().ToString("N");
        var signature = Sign(permitId, binding);
        _entries[permitId] = new Entry(binding, signature, consumed: false, issuedAt: Now);
        return new ExecutionPermit(this, permitId, signature, binding);
    }

    public Result<PermitConsumption> ConsumeAndCheck(string permitId, string signature, PermitBinding binding, PermitLiveState live)
    {
        if (!_entries.TryGetValue(permitId, out var entry))
            return Result<PermitConsumption>.Fail(RunErrors.PermitInvalidError());
        if (entry.Signature != signature)
            return Result<PermitConsumption>.Fail(RunErrors.PermitForgedError());
        if (entry.Consumed)
            return Result<PermitConsumption>.Fail(RunErrors.PermitAlreadyConsumedError());
        if (entry.Binding.ExpiresAtUtc < Now)
            return Result<PermitConsumption>.Fail(RunErrors.PermitExpiredError());
        if (entry.Binding.RevocationEpoch != CurrentRevocationEpoch)
            return Result<PermitConsumption>.Fail(RunErrors.PermitEpochChangedError());
        if (entry.Binding.LeaseExpiresAtUtc < Now)
            return Result<PermitConsumption>.Fail(RunErrors.PermitLeaseLostError());
        if (entry.Binding.WorkerLeaseOwner != live.WorkerLeaseOwner || entry.Binding.LeaseExpiresAtUtc < live.LeaseExpiresAtUtc)
            return Result<PermitConsumption>.Fail(RunErrors.PermitLeaseLostError());
        if (live.CancellationRequested)
            return Result<PermitConsumption>.Fail(RunErrors.PermitCancelledError());

        entry.Consumed = true;
        return Result<PermitConsumption>.Ok(new PermitConsumption(binding, DateTimeOffset.UtcNow));
    }

    public void Revoke(string permitId) => _entries.TryRemove(permitId, out _);

    // IRevocationEpoch: lets the policy admin bump the epoch when a save widens/restricts access,
    // invalidating every permit/receipt/grant issued at the prior epoch (doc 07 §11/§15/§17).
    long IRevocationEpoch.Current => CurrentRevocationEpoch;
    void IRevocationEpoch.Bump() => CurrentRevocationEpoch++;

    private string Sign(string permitId, PermitBinding b)
    {
        var data = Encoding.UTF8.GetBytes(
            $"{permitId}|{b.RunId}|{b.StepId}|{b.CapabilityStableId}|{b.ArgumentDigest}|{b.RevocationEpoch}|{b.WorkerLeaseOwner}");
        using var hmac = new HMACSHA256(_key);
        return Convert.ToHexString(hmac.ComputeHash(data));
    }
}
