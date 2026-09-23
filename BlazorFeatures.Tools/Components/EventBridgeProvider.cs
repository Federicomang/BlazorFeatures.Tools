using BlazorFeatures.Tools.Client.Events.Channels;
using Microsoft.AspNetCore.Components;

namespace BlazorFeatures.Tools.Client.Components;

public sealed class EventBridgeProvider : ComponentBase
{
    [Inject]
    private ILocalEventBridge Bridge { get; set; } = default!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Bridge.InitializeAsync();
        }
    }
}
