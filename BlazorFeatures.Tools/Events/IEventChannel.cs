namespace BlazorFeatures.Tools.Client.Events;

public interface IEventChannel<TChannel>
    where TChannel : class, IEventChannelContract
{
    string Name { get; }

    ValueTask<EventAuthorizationResult> AuthorizeJoinAsync(
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default);

    ValueTask<EventAuthorizationResult> AuthorizePublishAsync(
        IEventRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default);

    ValueTask<EventDispatchResult> DispatchAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default);
}

public interface IEventChannelMembership
{
    ValueTask<IAsyncDisposable> JoinAsync(
        EventChannelScope scope,
        CancellationToken cancellationToken = default);
}
