using BlazorFeatures.Abstractions;
using BlazorFeatures.Base.Extensions;
using BlazorFeatures.Base.Server;
using BlazorFeatures.Tools.Client.Events;
using BlazorFeatures.Tools.Server.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace BlazorFeatures.Tools.Tests;

public sealed class EventProductionBehaviorTests
{
    [Fact]
    public async Task Authorization_and_execution_use_the_same_feature_instance()
    {
        SameInstanceEventFeature.Reset();
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var response = await PublishAsync<SameInstanceEvent>(
            scope.ServiceProvider,
            "tests.same-instance",
            Guid.NewGuid());

        Assert.True(response.Success);
        Assert.NotNull(SameInstanceEventFeature.AuthorizationInstance);
        Assert.Equal(
            SameInstanceEventFeature.AuthorizationInstance,
            SameInstanceEventFeature.ExecutionInstance);
    }

    [Fact]
    public async Task A_successful_event_id_is_rejected_when_reused()
    {
        SameInstanceEventFeature.Reset();
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var eventId = Guid.NewGuid();

        var first = await PublishAsync<SameInstanceEvent>(
            scope.ServiceProvider,
            "tests.same-instance",
            eventId);
        var duplicate = await PublishAsync<SameInstanceEvent>(
            scope.ServiceProvider,
            "tests.same-instance",
            eventId);

        Assert.True(first.Success);
        Assert.False(duplicate.Success);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, SameInstanceEventFeature.Executions);
    }

    [Fact]
    public async Task A_failed_feature_does_not_consume_the_event_id()
    {
        FailingEventFeature.Executions = 0;
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var eventId = Guid.NewGuid();

        var first = await PublishAsync<FailingEvent>(
            scope.ServiceProvider,
            "tests.failing",
            eventId);
        var retry = await PublishAsync<FailingEvent>(
            scope.ServiceProvider,
            "tests.failing",
            eventId);

        Assert.False(first.Success);
        Assert.False(retry.Success);
        Assert.Equal(2, FailingEventFeature.Executions);
    }

    [Fact]
    public async Task Base_feature_authorization_uses_the_signalr_principal()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var httpContextAccessor = scope.ServiceProvider
            .GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = new DefaultHttpContext
        {
            User = Principal(new Claim("event_source", "ambient-http"))
        };
        var coordinator = scope.ServiceProvider.GetRequiredService<IEventCoordinator>();
        var request = new EventPublishRequest
        {
            EventId = Guid.NewGuid(),
            EventName = "tests.principal",
            EventVersion = 1,
            Scope = EventChannelScope.Default,
            Payload = JsonSerializer.SerializeToElement(new PrincipalEvent())
        };
        var context = new EventAuthorizationContext(
            Principal(new Claim("event_source", "signalr")),
            EventOperation.Publish,
            EventOrigin.SignalR,
            typeof(AllowedChannel),
            AllowedTestChannel.ChannelName,
            EventChannelScope.Default,
            "connection");

        var response = await coordinator.PublishAsync<AllowedChannel>(
            request,
            context,
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
    }

    [Fact]
    public async Task Server_router_delivers_only_to_the_matching_scope()
    {
        var router = new ServerEventRouter(
            Options.Create(new EventServerOptions()),
            NullLogger<ServerEventRouter>.Instance);
        var targetScope = EventChannelScope.Create("tenant", "one");
        var otherScope = EventChannelScope.Create("tenant", "two");
        var targetDelivery = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var otherDelivered = false;
        await using var targetLease = router.Register(
            typeof(AllowedChannel),
            targetScope,
            Principal(),
            (_, _) =>
            {
                targetDelivery.TrySetResult();
                return ValueTask.FromResult(EventDispatchResult.Empty);
            });
        await using var otherLease = router.Register(
            typeof(AllowedChannel),
            otherScope,
            Principal(),
            (_, _) =>
            {
                otherDelivered = true;
                return ValueTask.FromResult(EventDispatchResult.Empty);
            });

        var result = await router.DispatchAsync(
            typeof(AllowedChannel),
            Envelope(targetScope),
            TestContext.Current.CancellationToken);
        await targetDelivery.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Delivered);
        Assert.False(otherDelivered);
    }

    [Fact]
    public void Hub_binding_registry_rejects_duplicate_routes()
    {
        var registry = new EventHubBindingRegistry();
        registry.Add(typeof(AllowedChannel), typeof(TestHubMarker), "/hubs/test");

        Assert.Throws<InvalidOperationException>(() =>
            registry.Add(typeof(ForbiddenChannel), typeof(OtherHubMarker), "/hubs/test"));
    }

    private static async Task<BlazorFeatures.Abstractions.FeatureResponse<EventResponse>> PublishAsync<TEvent>(
        IServiceProvider serviceProvider,
        string eventName,
        Guid eventId)
        where TEvent : class, IEventRequest, new()
    {
        var coordinator = serviceProvider.GetRequiredService<IEventCoordinator>();
        return await coordinator.PublishAsync<AllowedChannel>(
            new EventPublishRequest
            {
                EventId = eventId,
                EventName = eventName,
                EventVersion = 1,
                Scope = EventChannelScope.Default,
                Payload = JsonSerializer.SerializeToElement(new TEvent()),
                CorrelationId = Guid.NewGuid()
            },
            new EventAuthorizationContext(
                Principal(),
                EventOperation.Publish,
                EventOrigin.SignalR,
                typeof(AllowedChannel),
                AllowedTestChannel.ChannelName,
                EventChannelScope.Default,
                "connection"),
            TestContext.Current.CancellationToken);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFeatures(config =>
        {
            config.AddAssemblyContaining<EventProductionBehaviorTests>();
            config.AddAssemblyContaining<IEventCoordinator>();
            config.AddAssemblyContaining<BlazorFeatures.Base.Server.ServerFeatureService>();
        });
        services.AddEventChannel<AllowedChannel, AllowedTestChannel>();
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(params Claim[] additionalClaims) => new(
        new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user"), .. additionalClaims],
            "Tests"));

    private static EventEnvelope Envelope(EventChannelScope scope) => new()
    {
        EventId = Guid.NewGuid(),
        EventName = "tests.same-instance",
        EventVersion = 1,
        ChannelName = AllowedTestChannel.ChannelName,
        Scope = scope,
        Payload = JsonSerializer.SerializeToElement(new SameInstanceEvent()),
        Origin = EventOrigin.System,
        OriginEndpointId = "tests",
        CorrelationId = Guid.NewGuid()
    };

    private sealed class TestHubMarker;
    private sealed class OtherHubMarker;
}

[EventName("tests.same-instance")]
public sealed class SameInstanceEvent : IEventRequest, IEventFor<AllowedChannel>;

public sealed class SameInstanceEventFeature : IEventFeature<SameInstanceEvent>
{
    private readonly Guid instanceId = Guid.NewGuid();
    public static Guid? AuthorizationInstance;
    public static Guid? ExecutionInstance;
    public static int Executions;

    public static void Reset()
    {
        AuthorizationInstance = null;
        ExecutionInstance = null;
        Executions = 0;
    }

    public ValueTask<EventAuthorizationResult> AuthorizeEventAsync(
        SameInstanceEvent eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        AuthorizationInstance = instanceId;
        return ValueTask.FromResult(EventAuthorizationResult.Allow());
    }

    public Task<FeatureResponse<EventResponse>> HandleClient(
        SameInstanceEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Execute();

    public Task<FeatureResponse<EventResponse>> HandleServer(
        SameInstanceEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Execute();

    private Task<FeatureResponse<EventResponse>> Execute()
    {
        ExecutionInstance = instanceId;
        Interlocked.Increment(ref Executions);
        return Task.FromResult(FeatureResponse<EventResponse>.AsSuccess(new()));
    }
}

[EventName("tests.failing")]
public sealed class FailingEvent : IEventRequest, IEventFor<AllowedChannel>;

public sealed class FailingEventFeature : IEventFeature<FailingEvent>
{
    public static int Executions;

    public ValueTask<EventAuthorizationResult> AuthorizeEventAsync(
        FailingEvent eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventAuthorizationResult.Allow());

    public Task<FeatureResponse<EventResponse>> HandleClient(
        FailingEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Execute();

    public Task<FeatureResponse<EventResponse>> HandleServer(
        FailingEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Execute();

    private static Task<FeatureResponse<EventResponse>> Execute()
    {
        Interlocked.Increment(ref Executions);
        return Task.FromResult(FeatureResponse<EventResponse>.AsFailure(
            messages: ["Expected failure."],
            statusCode: HttpStatusCode.BadRequest));
    }
}

[EventName("tests.principal")]
public sealed class PrincipalEvent : IEventRequest, IEventFor<AllowedChannel>;

public sealed class PrincipalEventFeature :
    IEventFeature<PrincipalEvent>,
    IBaseFeatureAuthorization
{
    public void BuildPolicy(AuthorizationPolicyBuilder builder)
        => builder.RequireClaim("event_source", "signalr");

    public ValueTask<EventAuthorizationResult> AuthorizeEventAsync(
        PrincipalEvent eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventAuthorizationResult.Allow());

    public Task<FeatureResponse<EventResponse>> HandleClient(
        PrincipalEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FeatureResponse<EventResponse>.AsSuccess(new()));

    public Task<FeatureResponse<EventResponse>> HandleServer(
        PrincipalEvent request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
        => Task.FromResult(FeatureResponse<EventResponse>.AsSuccess(new()));
}
