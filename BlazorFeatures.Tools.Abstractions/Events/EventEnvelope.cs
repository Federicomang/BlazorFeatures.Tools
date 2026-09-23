using System.Text.Json;

namespace BlazorFeatures.Tools.Events;

public sealed record EventPublishRequest
{
    public Guid EventId { get; init; }

    public required string EventName { get; init; }

    public required int EventVersion { get; init; }

    public EventChannelScope Scope { get; init; } = EventChannelScope.Default;

    public required JsonElement Payload { get; init; }

    public Guid CorrelationId { get; init; }
}

public sealed record EventEnvelope
{
    public required Guid EventId { get; init; }

    public required string EventName { get; init; }

    public required int EventVersion { get; init; }

    public required string ChannelName { get; init; }

    public EventChannelScope Scope { get; init; } = EventChannelScope.Default;

    public required JsonElement Payload { get; init; }

    public required EventOrigin Origin { get; init; }

    public required string OriginEndpointId { get; init; }

    public required Guid CorrelationId { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public int HopCount { get; init; }
}

public sealed record EventDeliveryContext(
    Guid EventId,
    Guid CorrelationId,
    string ChannelName,
    EventChannelScope Scope,
    EventOrigin Origin);

public sealed record EventDispatchResult(
    int Delivered,
    bool RemoteBroadcastAccepted,
    IReadOnlyList<Exception> Errors)
{
    public static EventDispatchResult Empty { get; } = new(0, false, []);
}
