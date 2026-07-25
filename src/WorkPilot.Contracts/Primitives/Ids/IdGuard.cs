using System;

namespace WorkPilot.Contracts.Primitives.Ids;

/// <summary>
/// Shared validation for strongly-typed entity identifiers. Keeps the allowed character set
/// and length bound in one place (the bound itself lives in versioned <see cref="Limits"/>).
/// </summary>
internal static class IdGuard
{
    public static string Normalize(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{kind} identifier must not be empty.", nameof(value));
        if (value.Length > Limits.V1_5.MaxEntityIdLength)
            throw new ArgumentException($"{kind} identifier exceeds maximum length {Limits.V1_5.MaxEntityIdLength}.", nameof(value));

        foreach (var c in value)
        {
            var ok = (c >= 'a' && c <= 'z')
                     || (c >= 'A' && c <= 'Z')
                     || (c >= '0' && c <= '9')
                     || c == '_'
                     || c == '-';
            if (!ok)
                throw new ArgumentException($"{kind} identifier contains an invalid character: '{c}'.", nameof(value));
        }

        return value;
    }
}
