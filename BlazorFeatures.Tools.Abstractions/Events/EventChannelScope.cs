namespace BlazorFeatures.Tools.Events;

public sealed record EventChannelScope
{
    public const string DefaultKind = "default";
    public const string DefaultValue = "default";

    public static EventChannelScope Default { get; } = new(DefaultKind, DefaultValue);

    public string Kind { get; init; }

    public string Value { get; init; }

    public EventChannelScope(string kind, string value)
    {
        Kind = ValidatePart(kind, nameof(kind));
        Value = ValidatePart(value, nameof(value));
    }

    public static EventChannelScope Create(string kind, string value) => new(kind, value);

    public override string ToString() => $"{Kind}/{Value}";

    internal static string ValidatePart(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
