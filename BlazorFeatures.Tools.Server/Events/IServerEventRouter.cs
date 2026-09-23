using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Threading.Channels;

namespace BlazorFeatures.Tools.Server.Events;

public interface IServerEventRouter
{
    IAsyncDisposable Register(
        Type channelType,
        EventChannelScope scope,
        ClaimsPrincipal user,
        Func<EventEnvelope, CancellationToken, ValueTask<EventDispatchResult>> receiver);

    ValueTask<EventDispatchResult> DispatchAsync(
        Type channelType,
        EventEnvelope envelope,
        CancellationToken cancellationToken = default);
}

internal sealed class ServerEventRouter(
    IOptions<EventServerOptions> options,
    ILogger<ServerEventRouter> logger) : IServerEventRouter
{
    private sealed class Endpoint
    {
        public required Guid Id { get; init; }
        public required ClaimsPrincipal User { get; init; }
        public required Channel<EventEnvelope> Queue { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public Task Worker { get; set; } = Task.CompletedTask;
    }

    private readonly object gate = new();
    private readonly Dictionary<(Type Channel, EventChannelScope Scope), List<Endpoint>> endpoints = [];

    public IAsyncDisposable Register(
        Type channelType,
        EventChannelScope scope,
        ClaimsPrincipal user,
        Func<EventEnvelope, CancellationToken, ValueTask<EventDispatchResult>> receiver)
    {
        ArgumentNullException.ThrowIfNull(channelType);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(receiver);

        var queue = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(
            options.Value.ServerSubscriberQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            User = user,
            Queue = queue,
            Cancellation = new CancellationTokenSource()
        };
        endpoint.Worker = ConsumeAsync(channelType, scope, endpoint, receiver);

        var key = (channelType, scope);
        lock (gate)
        {
            if (!endpoints.TryGetValue(key, out var registered))
                endpoints[key] = registered = [];
            registered.Add(endpoint);
        }

        return new RouterLease(() => RemoveAsync(key, endpoint));
    }

    public ValueTask<EventDispatchResult> DispatchAsync(
        Type channelType,
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Endpoint[] receivers;
        lock (gate)
        {
            receivers = endpoints.TryGetValue(
                (channelType, envelope.Scope),
                out var registered)
                ? [.. registered]
                : [];
        }

        var accepted = 0;
        foreach (var endpoint in receivers)
        {
            if (endpoint.Queue.Writer.TryWrite(envelope))
                accepted++;
            else
            {
                logger.LogWarning(
                    "Dropping event {EventId} for a slow server subscriber on channel {ChannelType} and scope {EventScope}",
                    envelope.EventId,
                    channelType.FullName,
                    envelope.Scope);
            }
        }

        return ValueTask.FromResult(new EventDispatchResult(accepted, false, []));
    }

    private async Task ConsumeAsync(
        Type channelType,
        EventChannelScope scope,
        Endpoint endpoint,
        Func<EventEnvelope, CancellationToken, ValueTask<EventDispatchResult>> receiver)
    {
        try
        {
            await foreach (var envelope in endpoint.Queue.Reader.ReadAllAsync(endpoint.Cancellation.Token))
            {
                try
                {
                    var result = await receiver(envelope, endpoint.Cancellation.Token);
                    foreach (var error in result.Errors)
                    {
                        logger.LogError(
                            error,
                            "A server event subscriber failed for event {EventId} on channel {ChannelType} and scope {EventScope}",
                            envelope.EventId,
                            channelType.FullName,
                            scope);
                    }
                }
                catch (OperationCanceledException) when (endpoint.Cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "A server event endpoint failed for event {EventId} on channel {ChannelType} and scope {EventScope}",
                        envelope.EventId,
                        channelType.FullName,
                        scope);
                }
            }
        }
        catch (OperationCanceledException) when (endpoint.Cancellation.IsCancellationRequested)
        {
        }
    }

    private async ValueTask RemoveAsync(
        (Type Channel, EventChannelScope Scope) key,
        Endpoint endpoint)
    {
        lock (gate)
        {
            if (endpoints.TryGetValue(key, out var registered))
            {
                registered.RemoveAll(item => item.Id == endpoint.Id);
                if (registered.Count == 0)
                    endpoints.Remove(key);
            }
        }

        endpoint.Queue.Writer.TryComplete();
        endpoint.Cancellation.Cancel();
        try
        {
            await endpoint.Worker;
        }
        finally
        {
            endpoint.Cancellation.Dispose();
        }
    }

    private sealed class RouterLease(Func<ValueTask> dispose) : IAsyncDisposable
    {
        private Func<ValueTask>? disposeAction = dispose;

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref disposeAction, null)?.Invoke()
                ?? ValueTask.CompletedTask;
    }
}
