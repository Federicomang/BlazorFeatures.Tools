using BlazorFeatures.Abstractions;
using BlazorFeatures.Base;
using BlazorFeatures.Base.Server.Extensions;
using BlazorFeatures.Tools.Client.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorFeatures.Tools.Server.Events;

public sealed class ServerEventRegistration : IFeatureRegistrationHandler
{
    public static void BeforeRegistration(IServiceCollection services)
    {
        services.AddBlazorFeatureEvents();
        services.AddOptions<EventServerOptions>()
            .Validate(options => options.MaximumEventNameLength > 0, "MaximumEventNameLength must be positive.")
            .Validate(options => options.MaximumScopePartLength > 0, "MaximumScopePartLength must be positive.")
            .Validate(options => options.MaximumPayloadBytes > 0, "MaximumPayloadBytes must be positive.")
            .Validate(options => options.DuplicateRetention > TimeSpan.Zero, "DuplicateRetention must be positive.")
            .Validate(options => options.MaximumDeduplicationEntries > 0, "MaximumDeduplicationEntries must be positive.")
            .Validate(options => options.ServerSubscriberQueueCapacity > 0, "ServerSubscriberQueueCapacity must be positive.")
            .Validate(options => options.MaximumScopesPerConnection > 0, "MaximumScopesPerConnection must be positive.")
            .ValidateOnStart();
        services.TryAddSingleton<IServerEventRouter, ServerEventRouter>();
        services.TryAddSingleton<IEventDeduplicationStore, InMemoryEventDeduplicationStore>();
        services.TryAddScoped<IEventCoordinator, EventCoordinator>();
        services.TryAddScoped<IServerEventPublisher, ServerEventPublisher>();
        services.AddServerFeatureBehavior<EventFeatureAuthorizationBehavior>();
        services.AddScoped<ServerEventManager>();
        services.AddScoped<IEventManager>(provider => provider.GetRequiredService<ServerEventManager>());
    }

    public static void AfterRegistration(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IFeatureCallerContextEnricher,
            EventFeatureCallerContextEnricher>());
    }
}

public static class EventServerServiceCollectionExtensions
{
    public static IServiceCollection ConfigureBlazorFeatureEvents(
        this IServiceCollection services,
        Action<EventServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        return services;
    }
}
