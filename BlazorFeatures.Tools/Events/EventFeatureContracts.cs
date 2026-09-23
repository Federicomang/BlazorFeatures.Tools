using BlazorFeatures.Base;
using BlazorFeatures.Abstractions;
using System.Net;

namespace BlazorFeatures.Tools.Client.Events;

public interface IEventFeature
{
    ValueTask<EventAuthorizationResult> AuthorizeEventAsync(
        IEventRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default);
}

public interface IEventFeature<TRequest> :
    IEventFeature,
    IBaseFeature<TRequest, EventResponse>
    where TRequest : class, IEventRequest
{
    ValueTask<EventAuthorizationResult> AuthorizeEventAsync(
        TRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default);

    ValueTask<EventAuthorizationResult> IEventFeature.AuthorizeEventAsync(
        IEventRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        if (eventRequest is not TRequest typedRequest)
        {
            throw new ArgumentException(
                $"Expected an event request of type {typeof(TRequest).FullName}.",
                nameof(eventRequest));
        }

        return AuthorizeEventAsync(typedRequest, context, cancellationToken);
    }
}

/// <summary>
/// Provides safe client-side defaults for an event feature. A distributed
/// event must override authorization and server handling in the server assembly.
/// </summary>
public abstract class EventFeature<TRequest> : IEventFeature<TRequest>
    where TRequest : class, IEventRequest
{
    public virtual ValueTask<EventAuthorizationResult> AuthorizeEventAsync(
        TRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventAuthorizationResult.Deny(
            "The event has no server authorization implementation."));

    public virtual Task<FeatureResponse<EventResponse>> HandleClient(
        TRequest request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FeatureResponse<EventResponse>.AsFailure(
            messages: ["Publish events through IEventManager."],
            statusCode: HttpStatusCode.BadRequest));

    public virtual Task<FeatureResponse<EventResponse>> HandleServer(
        TRequest request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FeatureResponse<EventResponse>.AsFailure(
            messages: ["The event has no server implementation."],
            statusCode: HttpStatusCode.NotImplemented));
}
