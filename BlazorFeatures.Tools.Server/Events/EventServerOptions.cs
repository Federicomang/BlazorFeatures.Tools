namespace BlazorFeatures.Tools.Server.Events;

public sealed class EventServerOptions
{
    public int MaximumEventNameLength { get; set; } = 128;

    public int MaximumScopePartLength { get; set; } = 128;

    public int MaximumPayloadBytes { get; set; } = 64 * 1024;

    public TimeSpan DuplicateRetention { get; set; } = TimeSpan.FromMinutes(10);

    public int MaximumDeduplicationEntries { get; set; } = 100_000;

    public int ServerSubscriberQueueCapacity { get; set; } = 256;

    public int MaximumScopesPerConnection { get; set; } = 32;

    public bool IncludePublishingConnection { get; set; } = true;
}
