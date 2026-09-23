using BlazorFeatures.Tools.Client.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlazorFeatures.Tools.Server.Events;

public static class EventHubRegistrationExtensions
{
    public static IServiceCollection AddSignalREventChannel<TChannel, TImplementation>(
        this IServiceCollection services)
        where TChannel : class, IEventChannelContract
        where TImplementation : class, IEventChannel<TChannel>
    {
        services.AddEventChannel<TChannel, TImplementation>();
        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumParallelInvocationsPerClient = 1;
            options.MaximumReceiveMessageSize ??= 128 * 1024;
        });
        services.TryAddSingleton<EventHubBindingRegistry>();
        return services;
    }

    public static HubEndpointConventionBuilder MapSignalREventHub<THub, TChannel>(
        this IEndpointRouteBuilder endpoints,
        string pattern)
        where THub : EventHub<TChannel>
        where TChannel : class, IEventChannelContract
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        endpoints.ServiceProvider
            .GetRequiredService<EventHubBindingRegistry>()
            .Add(typeof(TChannel), typeof(THub), pattern);
        return endpoints.MapHub<THub>(pattern);
    }
}

internal sealed class EventHubBindingRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<Type, (Type HubType, string Pattern)> byChannel = [];
    private readonly Dictionary<Type, Type> byHub = [];
    private readonly Dictionary<string, (Type ChannelType, Type HubType)> byPattern = new(StringComparer.OrdinalIgnoreCase);

    public void Add(Type channelType, Type hubType, string pattern)
    {
        lock (gate)
        {
            if (byChannel.TryGetValue(channelType, out var existingChannel))
            {
                if (existingChannel.HubType == hubType
                    && string.Equals(existingChannel.Pattern, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Event channel {channelType.FullName} and Hub {hubType.FullName} are already mapped to '{pattern}'.");
                }

                throw new InvalidOperationException(
                    $"Event channel {channelType.FullName} is already associated with Hub {existingChannel.HubType.FullName}.");
            }

            if (byHub.TryGetValue(hubType, out var existingHubChannel)
                && existingHubChannel != channelType)
            {
                throw new InvalidOperationException(
                    $"Event Hub {hubType.FullName} is already associated with channel {existingHubChannel.FullName}.");
            }

            if (byPattern.TryGetValue(pattern, out var existingPattern)
                && (existingPattern.ChannelType != channelType
                    || existingPattern.HubType != hubType))
            {
                throw new InvalidOperationException(
                    $"Event Hub route '{pattern}' is already associated with channel {existingPattern.ChannelType.FullName} and Hub {existingPattern.HubType.FullName}.");
            }

            byChannel[channelType] = (hubType, pattern);
            byHub[hubType] = channelType;
            byPattern[pattern] = (channelType, hubType);
        }
    }
}
