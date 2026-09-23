using BlazorFeatures.Abstractions;
using BlazorFeatures.Tools.Client.Events;
using BlazorFeatures.Tools.Client.Events.Channels;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace BlazorFeatures.Tools.Server.Events;

internal sealed class ServerEventManager(
    IServiceProvider serviceProvider,
    IEventDescriptorRegistry registry,
    IEventDispatcher dispatcher,
    AuthenticationStateProvider authenticationStateProvider,
    IServerEventRouter router,
    IOptions<JsonSerializerOptions>? jsonOptions = null) :
    EventManager(serviceProvider, registry, dispatcher, jsonOptions),
    IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<(Type Channel, EventChannelScope Scope), (int Count, IAsyncDisposable Lease)> routerLeases = [];

    public override async ValueTask<FeatureResponse<EventResponse>> PublishAsync<TChannel, TEvent>(
        EventChannelScope scope,
        TEvent eventRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(eventRequest);
        var descriptor = Registry.GetByRequest(typeof(TEvent));
        var channel = ServiceProvider.GetRequiredService<IEventChannel<TChannel>>();
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var authorizationContext = new EventAuthorizationContext(
            authenticationState.User,
            EventOperation.Publish,
            EventOrigin.InteractiveServer,
            typeof(TChannel),
            channel.Name,
            scope);

        if (typeof(TChannel) == typeof(LocalPageChannel))
        {
            var authorization = await channel.AuthorizePublishAsync(
                eventRequest,
                authorizationContext,
                cancellationToken);
            if (!authorization.Succeeded)
            {
                return FeatureResponse<EventResponse>.AsFailure(
                    messages: [authorization.FailureMessage ?? "Publishing to the local event channel was denied."],
                    statusCode: HttpStatusCode.Forbidden);
            }

            return await base.PublishAsync<TChannel, TEvent>(scope, eventRequest, cancellationToken);
        }

        var publishRequest = CreatePublishRequest(eventRequest, descriptor, scope);
        return await ServiceProvider.GetRequiredService<IEventCoordinator>()
            .PublishAsync<TChannel>(publishRequest, authorizationContext, cancellationToken);
    }

    public override async ValueTask<IAsyncDisposable> SubscribeAsync<TChannel, TEvent>(
        EventChannelScope scope,
        Func<TEvent, EventDeliveryContext, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(handler);
        var channel = ServiceProvider.GetRequiredService<IEventChannel<TChannel>>();
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var authorizationContext = new EventAuthorizationContext(
            authenticationState.User,
            EventOperation.Join,
            EventOrigin.InteractiveServer,
            typeof(TChannel),
            channel.Name,
            scope);
        var authorization = await channel.AuthorizeJoinAsync(
            authorizationContext,
            cancellationToken);
        if (!authorization.Succeeded)
        {
            throw new UnauthorizedAccessException(
                authorization.FailureMessage ?? "The event channel rejected the subscription.");
        }

        var localLease = Dispatcher.Subscribe<TChannel, TEvent>(scope, handler);
        if (typeof(TChannel) == typeof(LocalPageChannel))
        {
            if (channel is not IEventChannelMembership localMembership)
                return localLease;

            try
            {
                var membershipLease = await localMembership.JoinAsync(scope, cancellationToken);
                return new CompositeLease(localLease, membershipLease.DisposeAsync);
            }
            catch
            {
                await localLease.DisposeAsync();
                throw;
            }
        }

        try
        {
            EnsureRouterRegistration<TChannel>(scope, authenticationState.User);
            return new CompositeLease(
                localLease,
                () => ReleaseRouterRegistrationAsync(typeof(TChannel), scope));
        }
        catch
        {
            await localLease.DisposeAsync();
            throw;
        }
    }

    private void EnsureRouterRegistration<TChannel>(
        EventChannelScope scope,
        System.Security.Claims.ClaimsPrincipal user)
        where TChannel : class, IEventChannelContract
    {
        var key = (typeof(TChannel), scope);
        lock (gate)
        {
            if (routerLeases.TryGetValue(key, out var current))
            {
                routerLeases[key] = (current.Count + 1, current.Lease);
                return;
            }

            var lease = router.Register(
                typeof(TChannel),
                scope,
                user,
                (envelope, token) => Dispatcher.DispatchAsync(typeof(TChannel), envelope, token));
            routerLeases[key] = (1, lease);
        }
    }

    private async ValueTask ReleaseRouterRegistrationAsync(
        Type channelType,
        EventChannelScope scope)
    {
        IAsyncDisposable? lease = null;
        var key = (channelType, scope);
        lock (gate)
        {
            if (!routerLeases.TryGetValue(key, out var current))
                return;

            if (current.Count > 1)
            {
                routerLeases[key] = (current.Count - 1, current.Lease);
                return;
            }

            routerLeases.Remove(key);
            lease = current.Lease;
        }

        if (lease is not null)
            await lease.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] leases;
        lock (gate)
        {
            leases = [.. routerLeases.Values.Select(value => value.Lease)];
            routerLeases.Clear();
        }

        foreach (var lease in leases)
            await lease.DisposeAsync();
    }

    private sealed class CompositeLease(
        IAsyncDisposable localLease,
        Func<ValueTask> releaseRouter) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            await localLease.DisposeAsync();
            await releaseRouter();
        }
    }
}
