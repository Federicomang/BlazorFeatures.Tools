using BlazorFeatures.Abstractions;
using Microsoft.JSInterop;

namespace BlazorFeatures.Tools.Client.Events.Channels;

public abstract class SignalREventChannel<TChannel>(
    IJSRuntime jsRuntime,
    IEventDispatcher dispatcher) :
    IEventChannel<TChannel>,
    IEventChannelMembership,
    IAsyncDisposable
    where TChannel : class, IEventChannelContract
{
    private const string ModulePath = "./_content/BlazorFeatures.Tools.Client/blazorFeaturesEvents.js";
    private readonly SemaphoreSlim stateLock = new(1, 1);
    private readonly Dictionary<EventChannelScope, int> memberships = [];
    private readonly string endpointId = Guid.NewGuid().ToString("N");
    private IJSObjectReference? module;
    private DotNetObjectReference<SignalREventChannel<TChannel>>? dotNetReference;
    private bool disposed;

    public abstract string Name { get; }

    public abstract string HubUrl { get; }

    protected virtual ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<string?>(null);

    public virtual ValueTask<EventAuthorizationResult> AuthorizeJoinAsync(
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventAuthorizationResult.Allow());

    public virtual ValueTask<EventAuthorizationResult> AuthorizePublishAsync(
        IEventRequest eventRequest,
        EventAuthorizationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EventAuthorizationResult.Allow());

    public async ValueTask<IAsyncDisposable> JoinAsync(
        EventChannelScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await EnsureInitializedAsync(cancellationToken);
        await stateLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (memberships.TryGetValue(scope, out var count))
            {
                memberships[scope] = count + 1;
            }
            else
            {
                await module!.InvokeVoidAsync(
                    "joinSignalRScope",
                    cancellationToken,
                    endpointId,
                    scope);
                memberships.Add(scope, 1);
            }
        }
        finally
        {
            stateLock.Release();
        }

        return new MembershipLease(() => LeaveAsync(scope));
    }

    public async ValueTask<EventDispatchResult> DispatchAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var request = new EventPublishRequest
        {
            EventId = envelope.EventId,
            EventName = envelope.EventName,
            EventVersion = envelope.EventVersion,
            Scope = envelope.Scope,
            Payload = envelope.Payload,
            CorrelationId = envelope.CorrelationId
        };
        var response = await module!.InvokeAsync<FeatureResponse<EventResponse>>(
            "publishSignalREvent",
            cancellationToken,
            endpointId,
            request);

        if (!response.Success)
            throw new EventManager.EventPublishRejectedException(response);

        return new EventDispatchResult(
            response.Data?.InProcessDeliveriesAccepted ?? 0,
            response.Data?.RemoteBroadcastAccepted ?? false,
            []);
    }

    [JSInvokable]
    public async Task<string?> GetSignalRAccessTokenAsync()
        => await GetAccessTokenAsync(CancellationToken.None);

    [JSInvokable]
    public ValueTask<EventDispatchResult> ReceiveSignalREventAsync(EventEnvelope envelope)
        => dispatcher.DispatchAsync(typeof(TChannel), envelope);

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (module is not null)
            return;

        await stateLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (module is not null)
                return;

            var importedModule = await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                cancellationToken,
                ModulePath);
            var reference = DotNetObjectReference.Create(this);
            try
            {
                await importedModule.InvokeVoidAsync(
                    "registerSignalRChannel",
                    cancellationToken,
                    endpointId,
                    Name,
                    HubUrl,
                    reference);
                module = importedModule;
                dotNetReference = reference;
            }
            catch
            {
                reference.Dispose();
                await importedModule.DisposeAsync();
                throw;
            }
        }
        finally
        {
            stateLock.Release();
        }
    }

    private async ValueTask LeaveAsync(EventChannelScope scope)
    {
        await stateLock.WaitAsync();
        try
        {
            if (!memberships.TryGetValue(scope, out var count))
                return;

            if (count > 1)
            {
                memberships[scope] = count - 1;
                return;
            }

            memberships.Remove(scope);
            if (module is not null && !disposed)
            {
                await module.InvokeVoidAsync(
                    "leaveSignalRScope",
                    endpointId,
                    scope);
            }
        }
        catch (JSDisconnectedException)
        {
        }
        finally
        {
            stateLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stateLock.WaitAsync();
        try
        {
            if (disposed)
                return;

            disposed = true;
            memberships.Clear();
            if (module is not null)
            {
                try
                {
                    await module.InvokeVoidAsync("unregisterSignalRChannel", endpointId);
                    await module.DisposeAsync();
                }
                catch (JSDisconnectedException)
                {
                }
            }

            dotNetReference?.Dispose();
        }
        finally
        {
            stateLock.Release();
            stateLock.Dispose();
        }
    }

    private sealed class MembershipLease(Func<ValueTask> release) : IAsyncDisposable
    {
        private Func<ValueTask>? releaseAction = release;

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref releaseAction, null)?.Invoke()
                ?? ValueTask.CompletedTask;
    }
}
