using BlazorFeatures.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Claims;
using System.Text.Json;

namespace BlazorFeatures.Tools.Client.Events;

public class EventManager(
    IServiceProvider serviceProvider,
    IEventDescriptorRegistry registry,
    IEventDispatcher dispatcher,
    IOptions<JsonSerializerOptions>? jsonOptions = null) : IEventManager
{
    protected IServiceProvider ServiceProvider { get; } = serviceProvider;
    protected IEventDescriptorRegistry Registry { get; } = registry;
    protected IEventDispatcher Dispatcher { get; } = dispatcher;
    protected JsonSerializerOptions SerializerOptions { get; } =
        jsonOptions?.Value ?? new(JsonSerializerDefaults.Web);

    public virtual ValueTask<IAsyncDisposable> SubscribeAsync<TChannel, TEvent>(
        Func<TEvent, EventDeliveryContext, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>
        => SubscribeAsync<TChannel, TEvent>(EventChannelScope.Default, handler, cancellationToken);

    public virtual async ValueTask<IAsyncDisposable> SubscribeAsync<TChannel, TEvent>(
        EventChannelScope scope,
        Func<TEvent, EventDeliveryContext, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();

        IAsyncDisposable? membership = null;
        if (OperatingSystem.IsBrowser())
        {
            var channel = ServiceProvider.GetRequiredService<IEventChannel<TChannel>>();
            var authorization = await channel.AuthorizeJoinAsync(
                CreateClientAuthorizationContext<TChannel>(channel.Name, scope, EventOperation.Join),
                cancellationToken);
            if (!authorization.Succeeded)
            {
                throw new UnauthorizedAccessException(
                    authorization.FailureMessage ?? "The event channel rejected the subscription.");
            }

            if (channel is IEventChannelMembership joinableChannel)
                membership = await joinableChannel.JoinAsync(scope, cancellationToken);
        }

        try
        {
            var subscription = Dispatcher.Subscribe<TChannel, TEvent>(scope, handler);
            return membership is null
                ? subscription
                : new CompositeAsyncDisposable(subscription, membership);
        }
        catch
        {
            if (membership is not null)
                await membership.DisposeAsync();
            throw;
        }
    }

    public virtual ValueTask<FeatureResponse<EventResponse>> PublishAsync<TChannel, TEvent>(
        TEvent eventRequest,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>
        => PublishAsync<TChannel, TEvent>(EventChannelScope.Default, eventRequest, cancellationToken);

    public virtual async ValueTask<FeatureResponse<EventResponse>> PublishAsync<TChannel, TEvent>(
        EventChannelScope scope,
        TEvent eventRequest,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(eventRequest);
        var descriptor = Registry.GetByRequest(typeof(TEvent));
        if (!descriptor.ChannelTypes.Contains(typeof(TChannel)))
        {
            throw new InvalidOperationException(
                $"Event {typeof(TEvent).FullName} is not allowed on channel {typeof(TChannel).FullName}.");
        }

        var channel = ServiceProvider.GetRequiredService<IEventChannel<TChannel>>();
        if (OperatingSystem.IsBrowser())
        {
            var authorization = await channel.AuthorizePublishAsync(
                eventRequest,
                CreateClientAuthorizationContext<TChannel>(channel.Name, scope, EventOperation.Publish),
                cancellationToken);
            if (!authorization.Succeeded)
            {
                return FeatureResponse<EventResponse>.AsFailure(
                    messages: [authorization.FailureMessage ?? "Publishing to the event channel was denied."],
                    statusCode: HttpStatusCode.Forbidden);
            }
        }

        var envelope = CreateEnvelope(eventRequest, descriptor, channel.Name, scope);
        EventDispatchResult dispatch;
        try
        {
            dispatch = await channel.DispatchAsync(envelope, cancellationToken);
        }
        catch (EventPublishRejectedException exception)
        {
            return exception.Response;
        }
        var response = new EventResponse
        {
            EventId = envelope.EventId,
            InProcessDeliveriesAccepted = dispatch.Delivered,
            RemoteBroadcastAccepted = dispatch.RemoteBroadcastAccepted
        };
        return dispatch.Errors.Count == 0
            ? FeatureResponse<EventResponse>.AsSuccess(response)
            : FeatureResponse<EventResponse>.AsFailure(
                response,
                dispatch.Errors.Select(error => error.Message));
    }

    internal sealed class EventPublishRejectedException(
        FeatureResponse<EventResponse> response) : Exception
    {
        public FeatureResponse<EventResponse> Response { get; } = response;
    }

    protected EventEnvelope CreateEnvelope<TEvent>(
        TEvent eventRequest,
        EventDescriptor descriptor,
        string channelName,
        EventChannelScope scope)
        where TEvent : class, IEventRequest
        => new()
        {
            EventId = Guid.NewGuid(),
            EventName = descriptor.Name,
            EventVersion = descriptor.Version,
            ChannelName = channelName,
            Scope = scope,
            Payload = JsonSerializer.SerializeToElement(eventRequest, typeof(TEvent), SerializerOptions),
            Origin = OperatingSystem.IsBrowser()
                ? EventOrigin.WebAssembly
                : EventOrigin.InteractiveServer,
            OriginEndpointId = string.Empty,
            CorrelationId = Guid.NewGuid()
        };

    protected EventPublishRequest CreatePublishRequest<TEvent>(
        TEvent eventRequest,
        EventDescriptor descriptor,
        EventChannelScope scope)
        where TEvent : class, IEventRequest
        => new()
        {
            EventId = Guid.NewGuid(),
            EventName = descriptor.Name,
            EventVersion = descriptor.Version,
            Scope = scope,
            Payload = JsonSerializer.SerializeToElement(eventRequest, typeof(TEvent), SerializerOptions),
            CorrelationId = Guid.NewGuid()
        };

    private static EventAuthorizationContext CreateClientAuthorizationContext<TChannel>(
        string channelName,
        EventChannelScope scope,
        EventOperation operation)
        where TChannel : class, IEventChannelContract
        => new(
            new ClaimsPrincipal(new ClaimsIdentity()),
            operation,
            EventOrigin.WebAssembly,
            typeof(TChannel),
            channelName,
            scope);

    private sealed class CompositeAsyncDisposable(
        IAsyncDisposable first,
        IAsyncDisposable second) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }
}
