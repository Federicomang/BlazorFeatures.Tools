using BlazorFeatures.Abstractions;

namespace BlazorFeatures.Tools.Server.Events;

internal sealed class EventFeatureCallerContextEnricher : IFeatureCallerContextEnricher
{
    public ValueTask EnrichAsync(
        FeatureCallerContextBuilder context,
        CancellationToken cancellationToken = default)
    {
        if (context.Values.TryGetValue(
                EventFeatureContextKeys.AuthorizationContext,
                out var value)
            && value is EventAuthorizationContext eventContext)
        {
            context.User = eventContext.User;
        }

        return ValueTask.CompletedTask;
    }
}
