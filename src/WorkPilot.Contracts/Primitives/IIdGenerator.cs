namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// Produces unique identifiers. Injectable so executions can be made reproducible in tests.
/// </summary>
public interface IIdGenerator
{
    string NewId();
}
