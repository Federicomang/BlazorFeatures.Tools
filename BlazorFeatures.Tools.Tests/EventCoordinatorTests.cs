using BlazorFeatures.Abstractions;
using BlazorFeatures.Base.Extensions;
using BlazorFeatures.Tools.Client.Events;
using BlazorFeatures.Tools.Server.Events;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace BlazorFeatures.Tools.Tests;

public sealed class EventCoordinatorTests
{
    [Fact]
    public async Task Event_authorization_runs_before_the_feature()
    {
        DeniedEventFeature.Executions = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFeatures(config =>
        {
            config.AddAssemblyContaining<DeniedEventFeature>();
            config.AddAssemblyContaining<IEventCoordinator>();
            config.AddAssemblyContaining<BlazorFeatures.Base.Server.ServerFeatureService>();
        });
        services.AddEventChannel<AllowedChannel, AllowedTestChannel>();
        using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IEventCoordinator>();
        var publishRequest = new EventPublishRequest
        {
            EventId = Guid.NewGuid(),
            EventName = "tests.denied",
            EventVersion = 1,
            Scope = EventChannelScope.Default,
            Payload = JsonSerializer.SerializeToElement(new DeniedEvent()),
            CorrelationId = Guid.NewGuid()
        };
        var context = new EventAuthorizationContext(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user")], "Tests")),
            EventOperation.Publish,
            EventOrigin.SignalR,
            typeof(AllowedChannel),
            AllowedTestChannel.ChannelName,
            EventChannelScope.Default,
            "connection");

        var response = await coordinator.PublishAsync<AllowedChannel>(
            publishRequest,
            context,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, DeniedEventFeature.Executions);
    }
}

[EventName("tests.denied")]
public sealed class DeniedEvent : IEventRequest, IEventFor<AllowedChannel>;

public sealed class DeniedEventFeature : IEventFeature<DeniedEvent>
{
    public static int Executions;

    public ValueTask<EventAuthorizationResult> AuthorizeEventAsync(
        DeniedEvent eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventAuthorizationResult.Deny("Denied by the event feature."));

    public Task<FeatureResponse<EventResponse>> HandleClient(
        DeniedEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Execute();

    public Task<FeatureResponse<EventResponse>> HandleServer(
        DeniedEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Execute();

    private static Task<FeatureResponse<EventResponse>> Execute()
    {
        Interlocked.Increment(ref Executions);
        return Task.FromResult(FeatureResponse<EventResponse>.AsSuccess(new()));
    }
}

public sealed class AllowedTestChannel : IEventChannel<AllowedChannel>
{
    public const string ChannelName = "tests.allowed";

    public string Name => ChannelName;

    public ValueTask<EventAuthorizationResult> AuthorizeJoinAsync(
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventAuthorizationResult.Allow());

    public ValueTask<EventAuthorizationResult> AuthorizePublishAsync(
        IEventRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventAuthorizationResult.Allow());

    public ValueTask<EventDispatchResult> DispatchAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventDispatchResult.Empty);
}
