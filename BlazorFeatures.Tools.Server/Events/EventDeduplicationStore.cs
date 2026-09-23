using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace BlazorFeatures.Tools.Server.Events;

public enum EventDeduplicationResult
{
    Started,
    Duplicate,
    CapacityExceeded
}

public interface IEventDeduplicationStore
{
    ValueTask<EventDeduplicationResult> TryStartAsync(
        string channelName,
        EventChannelScope scope,
        Guid eventId,
        TimeSpan retention,
        CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(
        string channelName,
        EventChannelScope scope,
        Guid eventId,
        CancellationToken cancellationToken = default);
}

internal sealed class InMemoryEventDeduplicationStore(
    IOptions<EventServerOptions> options) : IEventDeduplicationStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> entries = new(StringComparer.Ordinal);

    public ValueTask<EventDeduplicationResult> TryStartAsync(
        string channelName,
        EventChannelScope scope,
        Guid eventId,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var key = CreateKey(channelName, scope, eventId);
        if (entries.TryGetValue(key, out var existingExpiration)
            && existingExpiration > now)
        {
            return ValueTask.FromResult(EventDeduplicationResult.Duplicate);
        }

        if (entries.Count >= options.Value.MaximumDeduplicationEntries)
        {
            RemoveExpired(now);
            if (entries.Count >= options.Value.MaximumDeduplicationEntries)
            {
                return ValueTask.FromResult(
                    EventDeduplicationResult.CapacityExceeded);
            }
        }

        while (true)
        {
            if (!entries.TryGetValue(key, out var expiresAt))
            {
                if (entries.TryAdd(key, now.Add(retention)))
                    return ValueTask.FromResult(EventDeduplicationResult.Started);
                continue;
            }

            if (expiresAt > now)
                return ValueTask.FromResult(EventDeduplicationResult.Duplicate);

            if (entries.TryUpdate(key, now.Add(retention), expiresAt))
                return ValueTask.FromResult(EventDeduplicationResult.Started);
        }
    }

    public ValueTask RemoveAsync(
        string channelName,
        EventChannelScope scope,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        entries.TryRemove(CreateKey(channelName, scope, eventId), out _);
        return ValueTask.CompletedTask;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var item in entries)
        {
            if (item.Value <= now)
                entries.TryRemove(item.Key, out _);
        }
    }

    private static string CreateKey(
        string channelName,
        EventChannelScope scope,
        Guid eventId)
        => string.Join('\u001f', channelName, scope.Kind, scope.Value, eventId.ToString("N"));
}
