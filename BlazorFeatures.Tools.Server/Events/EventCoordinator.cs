using BlazorFeatures.Abstractions;
using BlazorFeatures.Abstractions.Enums;
using BlazorFeatures.Base;
using BlazorFeatures.Tools.Client.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BlazorFeatures.Tools.Server.Events;

public interface IEventCoordinator
{
    ValueTask<FeatureResponse<EventResponse>> PublishAsync<TChannel>(
        EventPublishRequest request,
        EventAuthorizationContext authorizationContext,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract;
}

internal sealed class EventCoordinator(
    IServiceProvider serviceProvider,
    IFeatureService featureService,
    IEventDescriptorRegistry eventRegistry,
    IEventDeduplicationStore deduplicationStore,
    IOptions<EventServerOptions> options,
    IOptions<JsonSerializerOptions>? jsonOptions = null) : IEventCoordinator
{
    private readonly JsonSerializerOptions serializerOptions =
        jsonOptions?.Value ?? new(JsonSerializerDefaults.Web);

    public async ValueTask<FeatureResponse<EventResponse>> PublishAsync<TChannel>(
        EventPublishRequest request,
        EventAuthorizationContext authorizationContext,
        CancellationToken cancellationToken = default)
        where TChannel : class, IEventChannelContract
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorizationContext);

        var validationError = Validate(request);
        if (validationError is not null)
            return Failure(HttpStatusCode.BadRequest, validationError);

        EventDescriptor descriptor;
        IEventRequest eventRequest;
        try
        {
            descriptor = eventRegistry.Resolve(
                request.EventName,
                request.EventVersion,
                typeof(TChannel));
            eventRequest = request.Payload.Deserialize(
                descriptor.RequestType,
                serializerOptions) as IEventRequest
                ?? throw new JsonException("The event payload resolved to null.");
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException
            or InvalidOperationException)
        {
            return Failure(HttpStatusCode.BadRequest, "The event name, version or payload is invalid.");
        }

        var channel = serviceProvider.GetRequiredService<IEventChannel<TChannel>>();
        var trustedAuthorizationContext = authorizationContext with
        {
            ChannelType = typeof(TChannel),
            ChannelName = channel.Name,
            Scope = request.Scope
        };
        var channelAuthorization = await channel.AuthorizePublishAsync(
            eventRequest,
            trustedAuthorizationContext,
            cancellationToken);
        if (!channelAuthorization.Succeeded)
        {
            return Failure(
                HttpStatusCode.Forbidden,
                channelAuthorization.FailureMessage ?? "Publishing to the event channel was denied.");
        }

        var eventId = request.EventId == Guid.Empty ? Guid.NewGuid() : request.EventId;
        var deduplicationResult = await deduplicationStore.TryStartAsync(
            channel.Name,
            request.Scope,
            eventId,
            options.Value.DuplicateRetention,
            cancellationToken);
        if (deduplicationResult == EventDeduplicationResult.Duplicate)
        {
            return Failure(HttpStatusCode.Conflict, "The event has already been accepted.");
        }
        if (deduplicationResult == EventDeduplicationResult.CapacityExceeded)
        {
            return Failure(
                HttpStatusCode.ServiceUnavailable,
                "The event deduplication store is temporarily at capacity.");
        }

        var keepDeduplicationEntry = false;
        try
        {
            var correlationId = request.CorrelationId == Guid.Empty
                ? eventId
                : request.CorrelationId;
            var callerContext = new FeatureCallerContext(
                trustedAuthorizationContext.User,
                new Dictionary<string, object>
                {
                    [EventFeatureContextKeys.AuthorizationContext] =
                        trustedAuthorizationContext
                });
            var featureContext = new BaseFeatureContext(
                eventRequest,
                FeatureInvocationSource.Internal,
                correlationId,
                callerContext);
            featureContext.TempValues[EventFeatureContextKeys.AuthorizationContext] =
                trustedAuthorizationContext;

            var featureResponse = await featureService.Run<EventResponse>(
                eventRequest,
                featureContext,
                cancellationToken);
            if (!featureResponse.Success)
                return featureResponse;

            var envelope = new EventEnvelope
            {
                EventId = eventId,
                EventName = descriptor.Name,
                EventVersion = descriptor.Version,
                ChannelName = channel.Name,
                Scope = request.Scope,
                Payload = request.Payload.Clone(),
                Origin = trustedAuthorizationContext.Origin,
                OriginEndpointId = trustedAuthorizationContext.ConnectionId ?? string.Empty,
                CorrelationId = correlationId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                HopCount = 1
            };
            var dispatch = await channel.DispatchAsync(envelope, cancellationToken);
            keepDeduplicationEntry = true;
            var response = new EventResponse
            {
                EventId = eventId,
                InProcessDeliveriesAccepted = dispatch.Delivered,
                RemoteBroadcastAccepted = dispatch.RemoteBroadcastAccepted
            };

            return dispatch.Errors.Count == 0
                ? FeatureResponse<EventResponse>.Create(
                    true,
                    response,
                    featureResponse.Messages,
                    featureResponse.ValidationErrors,
                    featureResponse.StatusCode)
                : FeatureResponse<EventResponse>.AsFailure(
                    response,
                    ["One or more local event receivers rejected the event."],
                    statusCode: HttpStatusCode.InternalServerError);
        }
        finally
        {
            if (!keepDeduplicationEntry)
            {
                await deduplicationStore.RemoveAsync(
                    channel.Name,
                    request.Scope,
                    eventId,
                    CancellationToken.None);
            }
        }
    }

    private string? Validate(EventPublishRequest request)
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(request.EventName)
            || request.EventName.Length > configured.MaximumEventNameLength)
        {
            return "The event name is invalid.";
        }
        if (request.EventVersion <= 0)
            return "The event version must be positive.";
        if (request.Scope is null
            || string.IsNullOrWhiteSpace(request.Scope.Kind)
            || string.IsNullOrWhiteSpace(request.Scope.Value)
            || request.Scope.Kind.Length > configured.MaximumScopePartLength
            || request.Scope.Value.Length > configured.MaximumScopePartLength)
        {
            return "The event scope is invalid.";
        }
        if (request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "The event payload is required.";
        if (Encoding.UTF8.GetByteCount(request.Payload.GetRawText()) > configured.MaximumPayloadBytes)
            return "The event payload exceeds the configured limit.";
        return null;
    }

    private static FeatureResponse<EventResponse> Failure(
        HttpStatusCode statusCode,
        string message)
        => FeatureResponse<EventResponse>.AsFailure(
            messages: [message],
            statusCode: statusCode);
}
