using BlazorFeatures.Abstractions;
using BlazorFeatures.Tools.Client.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Json;

namespace BlazorFeatures.Tools.Server.Events;

public interface IServerEventPublisher
{
    ValueTask<FeatureResponse<EventResponse>> PublishAsync<TChannel, TEvent>(
        EventChannelScope scope,
        TEvent eventRequest,
        ClaimsPrincipal principal,
        EventOrigin origin = EventOrigin.System,
        Guid? eventId = null,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>;
}

internal sealed class ServerEventPublisher(
    IServiceProvider serviceProvider,
    IEventDescriptorRegistry registry,
    IEventCoordinator coordinator,
    IOptions<JsonSerializerOptions>? jsonOptions = null) : IServerEventPublisher
{
    private readonly JsonSerializerOptions serializerOptions =
        jsonOptions?.Value ?? new(JsonSerializerDefaults.Web);

    public ValueTask<FeatureResponse<EventResponse>> PublishAsync<TChannel, TEvent>(
        EventChannelScope scope,
        TEvent eventRequest,
        ClaimsPrincipal principal,
        EventOrigin origin = EventOrigin.System,
        Guid? eventId = null,
        Guid? correlationId = null,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
        where TEvent : class, IEventRequest, IEventFor<TChannel>
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(eventRequest);
        ArgumentNullException.ThrowIfNull(principal);
        if (origin is EventOrigin.Unknown or EventOrigin.WebAssembly or EventOrigin.SignalR)
        {
            throw new ArgumentOutOfRangeException(
                nameof(origin),
                "A server publisher must use the System or InteractiveServer origin.");
        }

        var descriptor = registry.GetByRequest(typeof(TEvent));
        if (!descriptor.ChannelTypes.Contains(typeof(TChannel)))
        {
            throw new InvalidOperationException(
                $"Event {typeof(TEvent).FullName} is not allowed on channel {typeof(TChannel).FullName}.");
        }

        var channel = serviceProvider.GetRequiredService<IEventChannel<TChannel>>();
        var actualEventId = eventId.GetValueOrDefault();
        if (actualEventId == Guid.Empty)
            actualEventId = Guid.NewGuid();
        var actualCorrelationId = correlationId.GetValueOrDefault();
        if (actualCorrelationId == Guid.Empty)
            actualCorrelationId = actualEventId;

        return coordinator.PublishAsync<TChannel>(
            new EventPublishRequest
            {
                EventId = actualEventId,
                EventName = descriptor.Name,
                EventVersion = descriptor.Version,
                Scope = scope,
                Payload = JsonSerializer.SerializeToElement(
                    eventRequest,
                    typeof(TEvent),
                    serializerOptions),
                CorrelationId = actualCorrelationId
            },
            new EventAuthorizationContext(
                principal,
                EventOperation.Publish,
                origin,
                typeof(TChannel),
                channel.Name,
                scope),
            cancellationToken);
    }
}
