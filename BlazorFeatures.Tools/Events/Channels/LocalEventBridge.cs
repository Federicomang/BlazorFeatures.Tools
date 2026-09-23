using Microsoft.JSInterop;

namespace BlazorFeatures.Tools.Client.Events.Channels;

internal sealed class LocalEventBridge(
    IJSRuntime jsRuntime,
    IEventDispatcher dispatcher) : ILocalEventBridge
{
    private const string ModulePath = "./_content/BlazorFeatures.Tools.Client/blazorFeaturesEvents.js";
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly string endpointId = Guid.NewGuid().ToString("N");
    private IJSObjectReference? module;
    private DotNetObjectReference<LocalEventBridge>? dotNetReference;
    private bool disposed;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (module is not null)
            return;

        await initializationLock.WaitAsync(cancellationToken);
        try
        {
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
                    "registerLocalEndpoint",
                    cancellationToken,
                    endpointId,
                    OperatingSystem.IsBrowser() ? "webassembly" : "server",
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
            initializationLock.Release();
        }
    }

    public async ValueTask<int> PublishAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await InitializeAsync(cancellationToken);

        return await module!.InvokeAsync<int>(
            "publishLocalEvent",
            cancellationToken,
            envelope with { OriginEndpointId = endpointId });
    }

    [JSInvokable]
    public ValueTask<EventDispatchResult> ReceiveLocalEventAsync(EventEnvelope envelope)
        => dispatcher.DispatchAsync(typeof(LocalPageChannel), envelope);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        disposed = true;
        if (module is not null)
        {
            try
            {
                await module.InvokeVoidAsync("unregisterLocalEndpoint", endpointId);
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        dotNetReference?.Dispose();
        initializationLock.Dispose();
    }
}
