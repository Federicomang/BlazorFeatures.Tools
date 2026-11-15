using BlazorFeatures.Tools.Server.Events;
using Xunit;

namespace BlazorFeatures.Tools.Tests;

public sealed class EventHubContractTests
{
    [Theory]
    [InlineData(nameof(EventHub<TestChannel>.Join))]
    [InlineData(nameof(EventHub<TestChannel>.Leave))]
    [InlineData(nameof(EventHub<TestChannel>.Publish))]
    public void SignalRMethodsExposeOnlyTheirWireArgument(string methodName)
    {
        var method = typeof(EventHub<TestChannel>).GetMethod(methodName);

        Assert.NotNull(method);
        Assert.Single(method.GetParameters());
    }

    private sealed class TestChannel : IEventChannelContract;
}
