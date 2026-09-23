using BlazorFeatures.Abstractions;
using BlazorFeatures.Base.Server;
using BlazorFeatures.Tools.Client.Events;
using System.Net;

namespace BlazorFeatures.Tools.Server.Events;

internal static class EventFeatureContextKeys
{
    public const string AuthorizationContext = "BlazorFeatures.Tools.EventAuthorizationContext";
}

internal sealed class EventFeatureAuthorizationBehavior : IServerFeatureBehavior
{
    public int Order => int.MinValue + 100;

    public async Task<FeatureResponse<TResponse>> HandleAsync<TResponse>(
        ServerFeatureExecutionContext<TResponse> context,
        ServerFeatureDelegate<TResponse> next,
        CancellationToken cancellationToken = default)
        where TResponse : class
    {
        if (context.Feature is not IEventFeature eventFeature)
            return await next();

        if (context.Request is not IEventRequest eventRequest
            || !context.FeatureContext.Values.TryGetValue(
                EventFeatureContextKeys.AuthorizationContext,
                out var value)
            || value is not EventAuthorizationContext authorizationContext)
        {
            return FeatureResponse<TResponse>.AsFailure(
                messages: ["Event features must be invoked through IEventManager."],
                statusCode: HttpStatusCode.Forbidden);
        }

        var authorization = await eventFeature.AuthorizeEventAsync(
            eventRequest,
            authorizationContext,
            cancellationToken);
        if (!authorization.Succeeded)
        {
            return FeatureResponse<TResponse>.AsFailure(
                messages: [authorization.FailureMessage ?? "Publishing the event was denied."],
                statusCode: HttpStatusCode.Forbidden);
        }

        return await next();
    }
}
