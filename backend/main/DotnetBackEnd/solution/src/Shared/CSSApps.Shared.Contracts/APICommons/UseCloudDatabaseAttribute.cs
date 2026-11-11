namespace CSSApps.Shared.Contracts.APICommons;

/// <summary>
/// Read-only HTTP methods (GET/HEAD/OPTIONS) normally read this host's local database replica. Apply this attribute
/// to a controller or action that must read authoritative data or write even though it uses a read-only method.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class UseCloudDatabaseAttribute : Attribute;
