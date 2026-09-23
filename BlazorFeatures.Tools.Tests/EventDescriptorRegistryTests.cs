using BlazorFeatures.Abstractions;
using BlazorFeatures.Abstractions.Enums;
using BlazorFeatures.Base;
using BlazorFeatures.Base.Attributes;
using BlazorFeatures.Base.Extensions;
using BlazorFeatures.Tools.Client.Events;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

[assembly: FeatureAssembly(RenderType.Server)]

namespace BlazorFeatures.Tools.Tests;

public sealed class EventDescriptorRegistryTests
{
    [Fact]
    public void Registry_accepts_only_declared_channels()
    {
        using var provider = CreateProvider();
        var registry = provider.GetRequiredService<IEventDescriptorRegistry>();

        var descriptor = registry.Resolve("tests.sample", 1, typeof(AllowedChannel));

        Assert.Equal(typeof(SampleEvent), descriptor.RequestType);
        Assert.Throws<InvalidOperationException>(() =>
            registry.Resolve("tests.sample", 1, typeof(ForbiddenChannel)));
    }

    [Fact]
    public async Task Dispatcher_delivers_a_typed_event()
    {
        using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<IEventDescriptorRegistry>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IEventDispatcher>();
        SampleEvent? received = null;
        await using var subscription = dispatcher.Subscribe<AllowedChannel, SampleEvent>(
            EventChannelScope.Default,
            (value, _, _) =>
            {
                received = value;
                return ValueTask.CompletedTask;
            });
        var descriptor = registry.GetByRequest(typeof(SampleEvent));
        var envelope = new EventEnvelope
        {
            EventId = Guid.NewGuid(),
            EventName = descriptor.Name,
            EventVersion = descriptor.Version,
            ChannelName = "tests.allowed",
            Payload = JsonSerializer.SerializeToElement(new SampleEvent { Value = 42 }),
            Origin = EventOrigin.System,
            OriginEndpointId = "tests",
            CorrelationId = Guid.NewGuid()
        };

        var result = await dispatcher.DispatchAsync(
            typeof(AllowedChannel),
            envelope,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Delivered);
        Assert.NotNull(received);
        Assert.Equal(42, received.Value);
    }

    [Fact]
    public async Task Dispatcher_isolates_equal_events_by_scope()
    {
        using var provider = CreateProvider();
        await using var serviceScope = provider.CreateAsyncScope();
        var dispatcher = serviceScope.ServiceProvider.GetRequiredService<IEventDispatcher>();
        var firstScope = EventChannelScope.Create("tenant", "one");
        var secondScope = EventChannelScope.Create("tenant", "two");
        var deliveries = 0;
        await using var subscription = dispatcher.Subscribe<AllowedChannel, SampleEvent>(
            firstScope,
            (_, _, _) =>
            {
                deliveries++;
                return ValueTask.CompletedTask;
            });
        var envelope = new EventEnvelope
        {
            EventId = Guid.NewGuid(),
            EventName = "tests.sample",
            EventVersion = 1,
            ChannelName = "tests.allowed",
            Scope = secondScope,
            Payload = JsonSerializer.SerializeToElement(new SampleEvent { Value = 42 }),
            Origin = EventOrigin.System,
            OriginEndpointId = "tests",
            CorrelationId = Guid.NewGuid()
        };

        var result = await dispatcher.DispatchAsync(
            typeof(AllowedChannel),
            envelope,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Delivered);
        Assert.Equal(0, deliveries);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddFeatures(config => config.AddAssemblyContaining<SampleEventFeature>());
        services.AddBlazorFeatureEvents();
        return services.BuildServiceProvider();
    }
}

public sealed class AllowedChannel : IEventChannelContract;

public sealed class ForbiddenChannel : IEventChannelContract;

[EventName("tests.sample")]
public sealed class SampleEvent : IEventRequest, IEventFor<AllowedChannel>
{
    public int Value { get; init; }
}

public sealed class SampleEventFeature : IEventFeature<SampleEvent>
{
    public ValueTask<EventAuthorizationResult> AuthorizeEventAsync(
        SampleEvent eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventAuthorizationResult.Allow());

    public Task<FeatureResponse<EventResponse>> HandleClient(
        SampleEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FeatureResponse<EventResponse>.AsSuccess(new()));

    public Task<FeatureResponse<EventResponse>> HandleServer(
        SampleEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FeatureResponse<EventResponse>.AsSuccess(new()));
}
