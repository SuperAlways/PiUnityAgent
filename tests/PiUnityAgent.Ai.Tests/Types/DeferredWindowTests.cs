using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class DeferredWindowTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("15m")]
    [InlineData("1h")]
    [InlineData("24h")]
    public void Allowed_Windows_Construct(string? window)
    {
        var request = new DeferredRequest.Enabled(window);
        Assert.Equal(window, request.Window);
    }

    [Fact]
    public void Invalid_Window_Throws_Exact_Message()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new DeferredRequest.Enabled("2h"));
        Assert.Equal(
            "Unknown deferred window: 2h. Expected 15m, 1h, or 24h.",
            error.Message);
    }
}
