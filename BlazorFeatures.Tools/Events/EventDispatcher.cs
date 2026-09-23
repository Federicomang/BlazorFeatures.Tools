using Microsoft.Extensions.Options;
using System.Text.Json;

namespace BlazorFeatures.Tools.Client.Events;

internal sealed class EventDispatcher(
    IEventDescriptorRegistry registry,
    IOptions<JsonSerializerOptions>? jsonOptions = null) : IEventDispatcher
{
    private sealed record Subscription(
        Guid Id,
        Func<object, EventDeliveryContext, CancellationToken, ValueTask> Handler);

    private readonly object gate = new();
    private readonly Dictionary<(Type Channel, Type Event, EventChannelScope Scope), List<Subscription>> subscriptions = [];
    private readonly JsonSerializerOptions serializerOptions = jsonOptions?.Value ?? new(JsonSerializerDefaults.Web);

    public IAsyncDisposable Subscribe<TChannel, TEvent>(
        EventChannelScope scope,
        Func<TEvent, EventDeliveryContext, CancellationToken, ValueTask> handler)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(handler);
        var descriptor = registry.GetByRequest(typeof(TEvent));
        EnsureChannel(descriptor, typeof(TChannel));

        var id = Guid.NewGuid();
        var key = (typeof(TChannel), typeof(TEvent), scope);
        lock (gate)
        {
            if (!subscriptions.TryGetValue(key, out var handlers))
                subscriptions[key] = handlers = [];

            handlers.Add(new Subscription(id, (value, context, token) =>
                handler((TEvent)value, context, token)));
        }

        return new SubscriptionLease(() => Remove(key, id));
    }

    public async ValueTask<EventDispatchResult> DispatchAsync(
        Type channelType,
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var descriptor = registry.Resolve(envelope.EventName, envelope.EventVersion, channelType);
        var eventRequest = envelope.Payload.Deserialize(
            descriptor.RequestType,
            serializerOptions) as IEventRequest
            ?? throw new JsonException($"Unable to deserialize event '{envelope.EventName}'.");

        Subscription[] handlers;
        lock (gate)
        {
            handlers = subscriptions.TryGetValue((channelType, descriptor.RequestType, envelope.Scope), out var registered)
                ? [.. registered]
                : [];
        }

        if (handlers.Length == 0)
            return EventDispatchResult.Empty;

        var context = new EventDeliveryContext(
            envelope.EventId,
            envelope.CorrelationId,
            envelope.ChannelName,
            envelope.Scope,
            envelope.Origin);
        List<Exception> errors = [];
        var delivered = 0;
        foreach (var subscription in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await subscription.Handler(eventRequest, context, cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        return new EventDispatchResult(delivered, false, errors);
    }

    private static void EnsureChannel(EventDescriptor descriptor, Type channelType)
    {
        if (!descriptor.ChannelTypes.Contains(channelType))
        {
            throw new InvalidOperationException(
                $"Event {descriptor.RequestType.FullName} is not allowed on channel {channelType.FullName}.");
        }
    }

    private void Remove((Type Channel, Type Event, EventChannelScope Scope) key, Guid id)
    {
        lock (gate)
        {
            if (!subscriptions.TryGetValue(key, out var handlers))
                return;

            handlers.RemoveAll(subscription => subscription.Id == id);
            if (handlers.Count == 0)
                subscriptions.Remove(key);
        }
    }

    private sealed class SubscriptionLease(Action dispose) : IAsyncDisposable
    {
        private Action? disposeAction = dispose;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposeAction, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
