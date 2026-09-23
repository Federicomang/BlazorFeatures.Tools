namespace BlazorFeatures.Tools.Client.Events;

using BlazorFeatures.Abstractions;

public interface IEventManager
{
    ValueTask<IAsyncDisposable> SubscribeAsync<TChannel, TEvent>(
        EventChannelScope scope,
        Func<TEvent, EventDeliveryContext, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>;

    ValueTask<IAsyncDisposable> SubscribeAsync<TChannel, TEvent>(
        Func<TEvent, EventDeliveryContext, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>;

    ValueTask<FeatureResponse<EventResponse>> PublishAsync<TChannel, TEvent>(
        EventChannelScope scope,
        TEvent eventRequest,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>;

    ValueTask<FeatureResponse<EventResponse>> PublishAsync<TChannel, TEvent>(
        TEvent eventRequest,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>;
}

public interface IEventDispatcher
{
    IAsyncDisposable Subscribe<TChannel, TEvent>(
        EventChannelScope scope,
        Func<TEvent, EventDeliveryContext, CancellationToken, ValueTask> handler)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>;

    ValueTask<EventDispatchResult> DispatchAsync(
        Type channelType,
        EventEnvelope envelope,
        CancellationToken cancellationToken = default);
}
