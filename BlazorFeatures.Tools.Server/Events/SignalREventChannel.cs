using BlazorFeatures.Tools.Client.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace BlazorFeatures.Tools.Server.Events;

public abstract class SignalREventChannel<TChannel, THub>(
    IServerEventRouter serverRouter,
    IHubContext<THub> hubContext,
    IOptions<EventServerOptions> options) : IEventChannel<TChannel>
    where TChannel : class, IEventChannelContract
    where THub : Hub
{
    public const string ReceiveMethodName = EventProtocol.ReceiveMethodName;

    public abstract string Name { get; }

    public abstract ValueTask<EventAuthorizationResult> AuthorizeJoinAsync(
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default);

    public abstract ValueTask<EventAuthorizationResult> AuthorizePublishAsync(
        IEventRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default);

    public virtual async ValueTask<EventDispatchResult> DispatchAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var serverResult = await serverRouter.DispatchAsync(
            typeof(TChannel),
            envelope,
            cancellationToken);
        var groupName = EventSignalRGroup.Create(Name, envelope.Scope);
        var clients = options.Value.IncludePublishingConnection
            || string.IsNullOrEmpty(envelope.OriginEndpointId)
            ? hubContext.Clients.Group(groupName)
            : hubContext.Clients.GroupExcept(groupName, [envelope.OriginEndpointId]);
        await clients.SendAsync(
            ReceiveMethodName,
            envelope,
            cancellationToken);

        return new EventDispatchResult(
            serverResult.Delivered,
            true,
            serverResult.Errors);
    }
}
