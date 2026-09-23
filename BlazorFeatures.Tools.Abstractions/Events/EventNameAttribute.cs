namespace BlazorFeatures.Tools.Events;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EventNameAttribute(string name, int version = 1) : Attribute
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("The event name cannot be empty.", nameof(name))
        : name;

    public int Version { get; } = version > 0
        ? version
        : throw new ArgumentOutOfRangeException(nameof(version), "The event version must be positive.");
}
