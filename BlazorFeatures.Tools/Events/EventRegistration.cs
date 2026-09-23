using BlazorFeatures.Base;
using BlazorFeatures.Tools.Client.Events.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorFeatures.Tools.Client.Events;

public static class EventServiceCollectionExtensions
{
    public static IServiceCollection AddBlazorFeatureEvents(this IServiceCollection services)
    {
        services.TryAddSingleton<IEventDescriptorRegistry, EventDescriptorRegistry>();
        services.TryAddScoped<EventDispatcher>();
        services.TryAddScoped<IEventDispatcher>(provider => provider.GetRequiredService<EventDispatcher>());
        services.TryAddScoped<EventManager>();
        services.TryAddScoped<IEventManager>(provider => provider.GetRequiredService<EventManager>());
        services.TryAddScoped<LocalEventBridge>();
        services.TryAddScoped<ILocalEventBridge>(provider => provider.GetRequiredService<LocalEventBridge>());
        services.TryAddScoped<IEventChannel<LocalPageChannel>, LocalPageChannel>();
        return services;
    }

    public static IServiceCollection AddEventChannel<TChannel, TImplementation>(this IServiceCollection services)
        where TChannel : class, IEventChannelContract
        where TImplementation : class, IEventChannel<TChannel>
    {
        services.AddScoped<TImplementation>();
        services.AddScoped<IEventChannel<TChannel>>(provider => provider.GetRequiredService<TImplementation>());
        return services;
    }
}

public sealed class ClientEventRegistration : IFeatureRegistrationHandler
{
    public static void BeforeRegistration(IServiceCollection services)
        => services.AddBlazorFeatureEvents();

    public static void AfterRegistration(IServiceCollection services)
    {
    }
}
