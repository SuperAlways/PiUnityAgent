using PiUnityAgent.Ai.Providers;

namespace PiUnityAgent.Ai.Tests.Providers;

public class ModelsErrorTests
{
    [Fact]
    public void Carries_Code_And_Cause_In_Message()
    {
        var inner = new InvalidOperationException("inner reason");
        var error = new ModelsError(ModelsErrorCode.Auth, "authentication failed", inner);

        Assert.Equal(ModelsErrorCode.Auth, error.Code);
        Assert.Contains("authentication failed", error.Message);
        Assert.Contains("inner reason", error.Message);
        Assert.Same(inner, error.InnerException);
    }
}
