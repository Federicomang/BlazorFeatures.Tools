using BlazorFeatures.Abstractions;
using BlazorFeatures.Tools.Client.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Claims;

namespace BlazorFeatures.Tools.Server.Events;

public abstract class EventHub<TChannel>(
    IEventChannel<TChannel> channel,
    IEventCoordinator coordinator,
    IOptions<EventServerOptions> options) : Hub
    where TChannel : class, IEventChannelContract
{
    private const string JoinedScopesKey = "BlazorFeatures.Tools.JoinedScopes";

    public async Task Join(EventChannelScope scope)
    {
        var cancellationToken = Context.ConnectionAborted;
        ValidateScope(scope);
        var authorization = await channel.AuthorizeJoinAsync(
            CreateAuthorizationContext(EventOperation.Join, scope),
            cancellationToken);
        if (!authorization.Succeeded)
            throw new HubException("Joining the event channel was denied.");

        var joinedScopes = GetJoinedScopes();
        if (!joinedScopes.Contains(scope)
            && joinedScopes.Count >= options.Value.MaximumScopesPerConnection)
        {
            throw new HubException("The connection has reached the event scope limit.");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            EventSignalRGroup.Create(channel.Name, scope),
            cancellationToken);
        joinedScopes.Add(scope);
    }

    public async Task Leave(EventChannelScope scope)
    {
        var cancellationToken = Context.ConnectionAborted;
        ValidateScope(scope);
        if (!GetJoinedScopes().Remove(scope))
            return;

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            EventSignalRGroup.Create(channel.Name, scope),
            cancellationToken);
    }

    public Task<FeatureResponse<EventResponse>> Publish(EventPublishRequest request)
    {
        var cancellationToken = Context.ConnectionAborted;
        ArgumentNullException.ThrowIfNull(request);
        return coordinator.PublishAsync<TChannel>(
            request,
            CreateAuthorizationContext(EventOperation.Publish, request.Scope),
            cancellationToken).AsTask();
    }

    private EventAuthorizationContext CreateAuthorizationContext(
        EventOperation operation,
        EventChannelScope scope)
        => new(
            Context.User ?? new ClaimsPrincipal(new ClaimsIdentity()),
            operation,
            EventOrigin.SignalR,
            typeof(TChannel),
            channel.Name,
            scope,
            Context.ConnectionId);

    private HashSet<EventChannelScope> GetJoinedScopes()
    {
        if (Context.Items.TryGetValue(JoinedScopesKey, out var value)
            && value is HashSet<EventChannelScope> scopes)
        {
            return scopes;
        }

        scopes = [];
        Context.Items[JoinedScopesKey] = scopes;
        return scopes;
    }

    private void ValidateScope(EventChannelScope scope)
    {
        if (scope is null
            || string.IsNullOrWhiteSpace(scope.Kind)
            || string.IsNullOrWhiteSpace(scope.Value)
            || scope.Kind.Length > options.Value.MaximumScopePartLength
            || scope.Value.Length > options.Value.MaximumScopePartLength)
        {
            throw new HubException("The event scope is invalid.");
        }
    }
}
