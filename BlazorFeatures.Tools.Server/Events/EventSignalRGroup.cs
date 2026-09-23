using System.Security.Cryptography;
using System.Text;

namespace BlazorFeatures.Tools.Server.Events;

internal static class EventSignalRGroup
{
    public static string Create(string channelName, EventChannelScope scope)
    {
        var input = Encoding.UTF8.GetBytes(
            string.Join('\u001f', channelName, scope.Kind, scope.Value));
        return $"bfe:{Convert.ToHexString(SHA256.HashData(input))}";
    }
}
