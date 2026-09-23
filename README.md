# BlazorFeatures.Tools events

Typed, authorized events across Blazor WebAssembly, Interactive Server, mixed render modes, and external SignalR clients.

## Packages

| Package | Purpose |
|---|---|
| `BlazorFeatures.Tools.Abstractions` | Stable wire contracts for shared projects and external .NET clients |
| `BlazorFeatures.Tools.Client` | `IEventManager`, local JavaScript bridge, browser SignalR transport |
| `BlazorFeatures.Tools.Server` | Event feature pipeline, direct server-circuit router, Hub transport |

All packages target .NET 10 and require BlazorFeatures 1.5.7.

## Delivery model

`IEventManager` is the single application API. Its implementation depends on the runtime:

```text
LocalPageChannel
  component -> .NET dispatcher -> JavaScript bridge -> another renderer in the same document

WebAssembly distributed channel
  component -> JavaScript SignalR client -> Hub -> event feature -> server circuits + SignalR group

Interactive Server distributed channel
  component -> event feature in-process -> server circuits + SignalR group
```

Interactive Server never opens a second application SignalR connection. Its subscriptions are registered directly in `IServerEventRouter`. The existing Blazor circuit is only used by Blazor to render the resulting UI changes.

Delivery is best-effort. A successful remote broadcast means that SignalR accepted the send, not that every remote handler completed. Persist critical commands before publishing them.

`EventResponse.InProcessDeliveriesAccepted` counts local handlers or server-circuit queues that accepted the event; it is not an end-to-end acknowledgement from remote clients.

## Define a channel and event

Place contracts in the client/shared project:

```csharp
using BlazorFeatures.Tools.Events;

public sealed class FactoryChannel : IEventChannelContract
{
}

[EventName("factory.machine-telemetry", version: 1)]
public sealed record MachineTelemetryReported(
    string DeviceId,
    decimal Temperature,
    DateTimeOffset MeasuredAtUtc)
    : IEventRequest,
      IEventFor<FactoryChannel>;
```

`EventName` is the stable wire identity. Do not use CLR type names as protocol names. `IEventFor<TChannel>` is an allowlist checked both at compile time and for untrusted Hub messages.

Every event must be discoverable as a BlazorFeatures feature. A client feature can inherit the safe default implementation:

```csharp
using BlazorFeatures.Tools.Client.Events;

public class MachineTelemetryFeature
    : EventFeature<MachineTelemetryReported>
{
}
```

The default implementation denies server execution. The server project supplies the real implementation:

```csharp
using BlazorFeatures.Abstractions;
using BlazorFeatures.Tools.Client.Events;
using BlazorFeatures.Tools.Events;

public sealed class MachineTelemetryServerFeature(
    ITelemetryRepository telemetry)
    : MachineTelemetryFeature
{
    public override ValueTask<EventAuthorizationResult> AuthorizeEventAsync(
        MachineTelemetryReported request,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        var deviceId = context.User.FindFirst("device_id")?.Value;
        return ValueTask.FromResult(deviceId == request.DeviceId
            ? EventAuthorizationResult.Allow()
            : EventAuthorizationResult.Deny("The device identity does not match the payload."));
    }

    public override async Task<FeatureResponse<EventResponse>> HandleServer(
        MachineTelemetryReported request,
        IFeatureContext featureContext,
        CancellationToken cancellationToken = default)
    {
        await telemetry.StoreAsync(request, cancellationToken);
        return FeatureResponse<EventResponse>.AsSuccess(new());
    }
}
```

`AuthorizeEventAsync` and `HandleServer` run on the same feature instance and in the same DI scope. The event is distributed only after `HandleServer` succeeds.

## Implement the browser channel

```csharp
using BlazorFeatures.Tools.Client.Events;
using BlazorFeatures.Tools.Client.Events.Channels;
using Microsoft.JSInterop;

public sealed class FactoryBrowserChannel(
    IJSRuntime jsRuntime,
    IEventDispatcher dispatcher)
    : SignalREventChannel<FactoryChannel>(jsRuntime, dispatcher)
{
    public override string Name => "factory";

    public override string HubUrl => "/hubs/events/factory";

    // Override GetAccessTokenAsync when the app uses bearer authentication.
    // Same-origin cookie authentication needs no override.
}
```

The official Microsoft SignalR 10.0.11 browser client is included in the RCL and loaded lazily. A connection is opened only when the channel is first published to or subscribed to. Active scopes are rejoined after reconnection.

## Implement the server channel and Hub

```csharp
using BlazorFeatures.Tools.Client.Events;
using BlazorFeatures.Tools.Server.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

public sealed class FactoryServerChannel(
    IServerEventRouter router,
    IHubContext<FactoryHub> hubContext,
    IOptions<EventServerOptions> options,
    IPlantAuthorization authorization)
    : SignalREventChannel<FactoryChannel, FactoryHub>(
        router,
        hubContext,
        options)
{
    public override string Name => "factory";

    public override async ValueTask<EventAuthorizationResult> AuthorizeJoinAsync(
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => await authorization.CanReadAsync(context.User, context.Scope, cancellationToken)
            ? EventAuthorizationResult.Allow()
            : EventAuthorizationResult.Deny();

    public override async ValueTask<EventAuthorizationResult> AuthorizePublishAsync(
        IEventRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => await authorization.CanPublishAsync(context.User, context.Scope, cancellationToken)
            ? EventAuthorizationResult.Allow()
            : EventAuthorizationResult.Deny();
}

public sealed class FactoryHub(
    IEventChannel<FactoryChannel> channel,
    IEventCoordinator coordinator,
    IOptions<EventServerOptions> options)
    : EventHub<FactoryChannel>(channel, coordinator, options)
{
}
```

The Hub has no business logic. It delegates join authorization to the channel and publication to the event coordinator.

## Register the host

Client project:

```csharp
builder.Services.AddFeatures(config =>
{
    config.AddAssemblyContaining<MyClientAssemblyMarker>();
    config.AddAssemblyContaining<BlazorFeaturesToolsClientAssemblyMarker>();
});

builder.Services.AddEventChannel<FactoryChannel, FactoryBrowserChannel>();
```

Server project:

```csharp
builder.Services.AddFeatures(config =>
{
    config.AddAssemblyContaining<MyClientAssemblyMarker>();
    config.AddAssemblyContaining<MyServerAssemblyMarker>();
    config.AddAssemblyContaining<BlazorFeaturesToolsClientAssemblyMarker>();
    config.AddAssemblyContaining<BlazorFeaturesToolsServerAssemblyMarker>();
});

builder.Services.AddSignalREventChannel<FactoryChannel, FactoryServerChannel>();

builder.Services.ConfigureBlazorFeatureEvents(options =>
{
    options.MaximumPayloadBytes = 64 * 1024;
    options.MaximumScopesPerConnection = 32;
    options.ServerSubscriberQueueCapacity = 256;
    options.DuplicateRetention = TimeSpan.FromMinutes(10);
    options.MaximumDeduplicationEntries = 100_000;
});
```

Map the Hub and apply an application policy:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("FactoryEvents", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "factory.events");
    });
});

app.MapSignalREventHub<FactoryHub, FactoryChannel>(
        "/hubs/events/factory")
    .RequireAuthorization("FactoryEvents");
```

Use `.RequireAuthorization()` when authentication alone is sufficient. Omit endpoint authorization only for a deliberately public channel; channel authorization still runs for every join and publish.

The binding registry enforces one channel to one Hub and rejects duplicate Hub routes.

## Publish and subscribe

Use a scope to isolate a tenant, room, plant, document, or device:

```csharp
var plantScope = EventChannelScope.Create("plant", plantId.ToString());

var response = await eventManager.PublishAsync<
    FactoryChannel,
    MachineTelemetryReported>(
        plantScope,
        new MachineTelemetryReported(
            "machine-17",
            72.5m,
            DateTimeOffset.UtcNow),
        cancellationToken);
```

Subscribe from a component and dispose the lease:

```csharp
private IAsyncDisposable? subscription;

protected override async Task OnInitializedAsync()
{
    subscription = await eventManager.SubscribeAsync<
        FactoryChannel,
        MachineTelemetryReported>(
            EventChannelScope.Create("plant", PlantId.ToString()),
            async (message, delivery, cancellationToken) =>
            {
                await InvokeAsync(() =>
                {
                    temperature = message.Temperature;
                    StateHasChanged();
                });
            });
}

public async ValueTask DisposeAsync()
{
    if (subscription is not null)
        await subscription.DisposeAsync();
}
```

Use `InvokeAsync` before changing component state because server events can be delivered outside the renderer synchronization context.

Overloads without a scope use `EventChannelScope.Default`.

Server background services, controllers, and features that aren't executing in a Blazor circuit can use `IServerEventPublisher` and must provide an explicit principal. This preserves the same channel and event authorization pipeline:

```csharp
await serverEvents.PublishAsync<FactoryChannel, MachineTelemetryReported>(
    plantScope,
    telemetry,
    servicePrincipal,
    EventOrigin.System,
    cancellationToken: cancellationToken);
```

## Local document events

Declare the local channel explicitly:

```csharp
[EventName("ui.cart.changed")]
public sealed record CartChanged(int Count)
    : IEventRequest,
      IEventFor<LocalPageChannel>;
```

```csharp
await eventManager.PublishAsync<LocalPageChannel, CartChanged>(
    new CartChanged(3));
```

The local bridge initializes lazily. `EventBridgeProvider` remains available when an application wants to initialize it eagerly, but is not required for normal publish/subscribe usage.

Local means the current browser document. It does not include other tabs or users.

## External .NET client or device

Reference `BlazorFeatures.Tools.Abstractions` plus the official `Microsoft.AspNetCore.SignalR.Client` package:

```csharp
var connection = new HubConnectionBuilder()
    .WithUrl("https://example.com/hubs/events/factory", options =>
    {
        options.AccessTokenProvider = tokenProvider.GetTokenAsync;
    })
    .WithAutomaticReconnect()
    .Build();

connection.On<EventEnvelope>(
    EventProtocol.ReceiveMethodName,
    envelope => HandleEventAsync(envelope));

await connection.StartAsync(cancellationToken);

var scope = EventChannelScope.Create("plant", "42");
await connection.InvokeAsync(EventProtocol.JoinMethodName, scope, cancellationToken);

var request = new EventPublishRequest
{
    EventId = Guid.NewGuid(),
    EventName = "factory.machine-telemetry",
    EventVersion = 1,
    Scope = scope,
    Payload = JsonSerializer.SerializeToElement(
        new MachineTelemetryReported(
            "machine-17",
            72.5m,
            DateTimeOffset.UtcNow)),
    CorrelationId = Guid.NewGuid()
};

FeatureResponse<EventResponse> result =
    await connection.InvokeAsync<FeatureResponse<EventResponse>>(
        EventProtocol.PublishMethodName,
        request,
        cancellationToken);
```

The server ignores client-controlled origin, channel name, connection ID, and server timestamp because these fields are not part of `EventPublishRequest`. They are added to `EventEnvelope` only after authorization and feature execution.

## Security boundaries

Authorization is deliberately layered:

1. endpoint policy: may the principal connect to this Hub;
2. `AuthorizeJoinAsync`: may it receive events for this scope;
3. `AuthorizePublishAsync`: may it publish into this scope;
4. optional BlazorFeatures `IBaseFeatureAuthorization`;
5. `AuthorizeEventAsync`: may it invoke this specific event and payload.

The SignalR principal is propagated to both BlazorFeatures authorization layers. Browser-side authorization is advisory only; all authoritative checks run again on the server.

Membership in a channel scope grants visibility to every event sent to that scope. Put events with different read permissions in a different channel or narrower scope.

Configure ASP.NET Core rate limiting on Hub endpoints exposed to untrusted networks. The package additionally limits payload size, scope length, scopes per connection, concurrent Hub invocations, and duplicate event IDs.

## Reliability and scale-out

The default `IServerEventRouter` is process-local and uses one bounded queue per Interactive Server endpoint. A slow circuit cannot block another circuit; when its queue is full, new delivery attempts are dropped and logged.

For multiple application nodes, replace both:

- SignalR transport with Azure SignalR or a SignalR backplane;
- `IServerEventRouter` and `IEventDeduplicationStore` with distributed implementations.

SignalR is not durable storage. For offline devices or critical commands, persist the command and use SignalR as the live notification path. A device should acknowledge completion with a separate event carrying the original correlation ID.

## Protocol defaults

- JSON wire format with web defaults;
- event names and versions are explicit;
- default maximum payload: 64 KiB;
- default maximum scopes per connection: 32;
- duplicate successful event IDs retained for 10 minutes;
- publisher receives its own event by default;
- server-circuit queues contain 256 events by default;
- no durable replay.
