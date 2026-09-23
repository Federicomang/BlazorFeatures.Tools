namespace BlazorFeatures.Tools.Events;

/// <summary>
/// Stable SignalR method names used by external event clients.
/// </summary>
public static class EventProtocol
{
    public const string JoinMethodName = "Join";

    public const string LeaveMethodName = "Leave";

    public const string PublishMethodName = "Publish";

    public const string ReceiveMethodName = "BlazorFeaturesReceiveEvent";
}
