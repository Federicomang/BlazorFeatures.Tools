namespace BlazorFeatures.Tools.Client.Events.Channels;

public class LocalPageChannel(
    IEventDispatcher dispatcher,
    ILocalEventBridge bridge) :
    IEventChannel<LocalPageChannel>,
    IEventChannelMembership,
    IEventChannelContract
{
    public const string ChannelName = "local.page";

    public string Name => ChannelName;

    public virtual ValueTask<EventAuthorizationResult> AuthorizeJoinAsync(
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(EventAuthorizationResult.Allow());
    }

    public virtual ValueTask<EventAuthorizationResult> AuthorizePublishAsync(
        IEventRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventRequest);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(EventAuthorizationResult.Allow());
    }

    public virtual async ValueTask<EventDispatchResult> DispatchAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var localResult = await dispatcher.DispatchAsync(
            typeof(LocalPageChannel),
            envelope,
            cancellationToken);
        var remoteDeliveries = await bridge.PublishAsync(envelope, cancellationToken);

        return new EventDispatchResult(
            localResult.Delivered + remoteDeliveries,
            false,
            localResult.Errors);
    }

    public async ValueTask<IAsyncDisposable> JoinAsync(
        EventChannelScope scope,
        CancellationToken cancellationToken = default)
    {
        await bridge.InitializeAsync(cancellationToken);
        return NoopLease.Instance;
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public interface ILocalEventBridge : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<int> PublishAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default);
}
