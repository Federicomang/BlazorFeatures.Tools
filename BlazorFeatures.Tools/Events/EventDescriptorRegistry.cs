using BlazorFeatures.Base;
using System.Collections.ObjectModel;
using System.Reflection;

namespace BlazorFeatures.Tools.Client.Events;

public sealed record EventDescriptor(
    string Name,
    int Version,
    Type RequestType,
    IReadOnlySet<Type> ChannelTypes);

public interface IEventDescriptorRegistry
{
    IReadOnlyList<EventDescriptor> Events { get; }

    EventDescriptor GetByRequest(Type requestType);

    EventDescriptor Resolve(string eventName, int version, Type channelType);
}

internal sealed class EventDescriptorRegistry : IEventDescriptorRegistry
{
    private readonly IReadOnlyDictionary<Type, EventDescriptor> byRequest;
    private readonly IReadOnlyDictionary<(string Name, int Version), EventDescriptor> byWireIdentity;

    public IReadOnlyList<EventDescriptor> Events { get; }

    public EventDescriptorRegistry(IFeatureRegistry featureRegistry)
    {
        var descriptors = featureRegistry.Features
            .Where(feature => typeof(IEventRequest).IsAssignableFrom(feature.RequestType))
            .Select(feature => feature.RequestType)
            .Distinct()
            .Select(CreateDescriptor)
            .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Version)
            .ToArray();

        var duplicate = descriptors
            .GroupBy(descriptor => (descriptor.Name, descriptor.Version))
            .FirstOrDefault(group => group.Select(item => item.RequestType).Distinct().Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Multiple events use the wire identity '{duplicate.Key.Name}' version {duplicate.Key.Version}.");
        }

        Events = Array.AsReadOnly(descriptors);
        byRequest = new ReadOnlyDictionary<Type, EventDescriptor>(
            descriptors.ToDictionary(descriptor => descriptor.RequestType));
        byWireIdentity = new ReadOnlyDictionary<(string Name, int Version), EventDescriptor>(
            descriptors.ToDictionary(descriptor => (descriptor.Name, descriptor.Version)));
    }

    public EventDescriptor GetByRequest(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        return byRequest.TryGetValue(requestType, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException($"Event request {requestType.FullName} was not discovered as a feature.");
    }

    public EventDescriptor Resolve(string eventName, int version, Type channelType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(channelType);

        if (!byWireIdentity.TryGetValue((eventName, version), out var descriptor))
            throw new InvalidOperationException($"Unknown event '{eventName}' version {version}.");

        if (!descriptor.ChannelTypes.Contains(channelType))
        {
            throw new InvalidOperationException(
                $"Event '{eventName}' version {version} is not allowed on channel {channelType.FullName}.");
        }

        return descriptor;
    }

    private static EventDescriptor CreateDescriptor(Type requestType)
    {
        var name = requestType.GetCustomAttribute<EventNameAttribute>()
            ?? throw new InvalidOperationException(
                $"Event request {requestType.FullName} must declare {nameof(EventNameAttribute)}.");
        var channels = requestType.GetInterfaces()
            .Where(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEventFor<>))
            .Select(candidate => candidate.GetGenericArguments()[0])
            .ToHashSet();

        if (channels.Count == 0)
        {
            throw new InvalidOperationException(
                $"Event request {requestType.FullName} must implement at least one IEventFor<TChannel> contract.");
        }

        return new EventDescriptor(name.Name, name.Version, requestType, channels);
    }
}
