using BlazorFeatures.Abstractions;
using System.Security.Claims;

namespace BlazorFeatures.Tools.Events;

public sealed class EventResponse
{
    public Guid EventId { get; init; }

    public int InProcessDeliveriesAccepted { get; init; }

    public bool RemoteBroadcastAccepted { get; init; }
}

public interface IEventRequest : IBaseFeatureRequest<EventResponse>
{
}

public interface IEventChannelContract
{
}

public interface IEventFor<TChannel>
    where TChannel : class, IEventChannelContract
{
}

public enum EventOperation
{
    Join,
    Publish
}

public enum EventOrigin
{
    Unknown,
    WebAssembly,
    InteractiveServer,
    SignalR,
    System
}

public sealed record EventAuthorizationContext(
    ClaimsPrincipal User,
    EventOperation Operation,
    EventOrigin Origin,
    Type ChannelType,
    string ChannelName,
    EventChannelScope Scope,
    string? ConnectionId = null);

public sealed record EventAuthorizationResult(bool Succeeded, string? FailureMessage = null)
{
    public static EventAuthorizationResult Allow() => new(true);

    public static EventAuthorizationResult Deny(string? message = null) => new(false, message);
}
