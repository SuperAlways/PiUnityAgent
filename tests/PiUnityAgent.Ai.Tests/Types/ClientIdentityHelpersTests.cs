using System.Reflection;
using PiUnityAgent.Ai.Types;

namespace PiUnityAgent.Ai.Tests.Types;

public class ClientIdentityHelpersTests
{
    private static readonly ClientIdentityTarget OpenRouter = new("openrouter", "https://openrouter.ai/api/v1");
    private static readonly ClientIdentityTarget Nvidia = new("nvidia", "https://integrate.api.nvidia.com/v1");
    private static readonly ClientIdentityTarget OpenCode = new("opencode", "https://opencode.ai/api/v1");
    private static readonly ClientIdentityTarget Other = new("some-provider", "https://api.example.com/v1");

    [Fact]
    public void Identity_Headers_Map_OpenRouter_And_UserAgent()
    {
        var headers = ClientIdentityHelpers.GetIdentityHeaders(
            new ClientIdentityTarget("openrouter", "https://openrouter.ai/api/v1"),
            new ClientIdentity
            {
                UserAgent = UserAgentMode.Custom("test/1.0"),
                Attribution = new ClientAttribution("app", "https://app.test", new[] { "cli-agent" }),
            });
        Assert.Equal("test/1.0", headers["User-Agent"]);
        Assert.Equal("https://app.test", headers["HTTP-Referer"]);
        Assert.Equal("cli-agent", headers["X-OpenRouter-Categories"]);
    }

    [Fact]
    public void FormatUserAgent_Default_Is_Detailed_Pi_With_Browser_Runtime()
    {
        Assert.Equal($"pi/{ClientIdentityHelpers.GetClientVersion()} (browser)", ClientIdentityHelpers.FormatUserAgent());
        Assert.Equal(
            $"pi/{ClientIdentityHelpers.GetClientVersion()} (browser)",
            ClientIdentityHelpers.FormatUserAgent(new ClientIdentity { UserAgent = UserAgentMode.Detailed }));
    }

    [Fact]
    public void FormatUserAgent_Short_Is_Pi()
    {
        Assert.Equal("pi", ClientIdentityHelpers.FormatUserAgent(new ClientIdentity { UserAgent = UserAgentMode.Short }));
    }

    [Fact]
    public void FormatUserAgent_Custom_Wins_Over_Short_And_Detailed()
    {
        Assert.Equal(
            "custom/2.0 (mine)",
            ClientIdentityHelpers.FormatUserAgent(new ClientIdentity { UserAgent = UserAgentMode.Custom("custom/2.0 (mine)") }));
    }

    [Fact]
    public void FormatUserAgent_Uses_Identity_Version()
    {
        Assert.Equal(
            "pi/9.9.9 (browser)",
            ClientIdentityHelpers.FormatUserAgent(new ClientIdentity { Version = "9.9.9" }));
    }

    [Fact]
    public void GetClientVersion_Defaults_To_Ai_Assembly_Version()
    {
        var assemblyVersion = typeof(ClientIdentity).Assembly.GetName().Version;
        Assert.NotNull(assemblyVersion);
        var expected = assemblyVersion.Build >= 0
            ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}";

        Assert.Equal(expected, ClientIdentityHelpers.GetClientVersion());
        Assert.Equal("9.9.9", ClientIdentityHelpers.GetClientVersion(new ClientIdentity { Version = "9.9.9" }));
    }

    [Fact]
    public void Attribution_OpenRouter_Maps_Title_And_Joined_Categories()
    {
        var headers = ClientIdentityHelpers.GetAttributionHeaders(
            OpenRouter,
            new ClientIdentity
            {
                Attribution = new ClientAttribution("app", "https://app.test", new[] { "a", "b" }),
            });

        Assert.Equal("https://app.test", headers["HTTP-Referer"]);
        Assert.Equal("app", headers["X-OpenRouter-Title"]);
        Assert.Equal("a,b", headers["X-OpenRouter-Categories"]);
    }

    [Fact]
    public void Attribution_OpenRouter_Without_Categories_Omits_The_Header()
    {
        var withNull = ClientIdentityHelpers.GetAttributionHeaders(
            OpenRouter, new ClientIdentity { Attribution = new ClientAttribution("app", "https://app.test") });
        var withEmpty = ClientIdentityHelpers.GetAttributionHeaders(
            OpenRouter,
            new ClientIdentity { Attribution = new ClientAttribution("app", "https://app.test", Array.Empty<string>()) });

        Assert.Equal("app", withNull["X-OpenRouter-Title"]);
        Assert.False(withNull.ContainsKey("X-OpenRouter-Categories"));
        Assert.Equal("app", withEmpty["X-OpenRouter-Title"]);
        Assert.False(withEmpty.ContainsKey("X-OpenRouter-Categories"));
    }

    [Fact]
    public void Attribution_OpenRouter_Matches_By_BaseUrl_Too()
    {
        var headers = ClientIdentityHelpers.GetAttributionHeaders(
            new ClientIdentityTarget("custom-provider", "https://openrouter.ai/api/v1"),
            new ClientIdentity { Attribution = new ClientAttribution("app", "https://app.test") });

        Assert.Equal("https://app.test", headers["HTTP-Referer"]);
        Assert.Equal("app", headers["X-OpenRouter-Title"]);
    }

    [Fact]
    public void Attribution_Nvidia_Capitalizes_The_Origin()
    {
        var headers = ClientIdentityHelpers.GetAttributionHeaders(
            Nvidia, new ClientIdentity { Attribution = new ClientAttribution("app", "https://app.test") });

        Assert.Equal("App", headers["X-BILLING-INVOKE-ORIGIN"]);
        Assert.Single(headers);
    }

    [Fact]
    public void Attribution_Nvidia_Matches_By_Host_Too()
    {
        var headers = ClientIdentityHelpers.GetAttributionHeaders(
            new ClientIdentityTarget("custom-provider", "https://integrate.api.nvidia.com/v1"),
            new ClientIdentity { Attribution = new ClientAttribution("pi", "https://pi.dev") });

        Assert.Equal("Pi", headers["X-BILLING-INVOKE-ORIGIN"]);
    }

    [Fact]
    public void Attribution_OpenCode_Maps_Client_Header()
    {
        var byProvider = ClientIdentityHelpers.GetAttributionHeaders(
            OpenCode, new ClientIdentity { Attribution = new ClientAttribution("app", "https://app.test") });
        var byGoProvider = ClientIdentityHelpers.GetAttributionHeaders(
            new ClientIdentityTarget("opencode-go", "https://api.example.com/v1"),
            new ClientIdentity { Attribution = new ClientAttribution("app", "https://app.test") });
        var byHost = ClientIdentityHelpers.GetAttributionHeaders(
            new ClientIdentityTarget("custom-provider", "https://opencode.ai/v1"),
            new ClientIdentity { Attribution = new ClientAttribution("app", "https://app.test") });

        Assert.Equal("app", byProvider["x-opencode-client"]);
        Assert.Equal("app", byGoProvider["x-opencode-client"]);
        Assert.Equal("app", byHost["x-opencode-client"]);
    }

    [Fact]
    public void Attribution_Unmatched_Target_Or_Turned_Off_Is_Empty()
    {
        var unmatched = ClientIdentityHelpers.GetAttributionHeaders(
            Other, new ClientIdentity { Attribution = new ClientAttribution("app", "https://app.test") });
        var turnedOff = ClientIdentityHelpers.GetAttributionHeaders(
            OpenRouter, new ClientIdentity { Attribution = null });
        var noIdentity = ClientIdentityHelpers.GetAttributionHeaders(OpenRouter);

        Assert.Empty(unmatched);
        Assert.Empty(turnedOff);
        Assert.Empty(noIdentity);
    }

    [Fact]
    public void Identity_Headers_For_Unmatched_Target_Carry_Only_UserAgent()
    {
        var headers = ClientIdentityHelpers.GetIdentityHeaders(
            Other,
            new ClientIdentity
            {
                Attribution = new ClientAttribution("app", "https://app.test", new[] { "cli-agent" }),
            });

        Assert.Single(headers);
        Assert.Equal($"pi/{ClientIdentityHelpers.GetClientVersion()} (browser)", headers["User-Agent"]);
    }

    [Fact]
    public void Identity_Headers_Without_Identity_Use_Defaults()
    {
        var headers = ClientIdentityHelpers.GetIdentityHeaders(OpenRouter);

        Assert.Single(headers);
        Assert.Equal($"pi/{ClientIdentityHelpers.GetClientVersion()} (browser)", headers["User-Agent"]);
    }

    [Fact]
    public void OpenCode_Session_Headers_Map_Only_For_OpenCode_With_Session()
    {
        var session = ClientIdentityHelpers.GetOpenCodeSessionHeaders(OpenCode, "conv-1");
        var byHost = ClientIdentityHelpers.GetOpenCodeSessionHeaders(
            new ClientIdentityTarget("custom-provider", "https://opencode.ai/v1"), "conv-1");

        Assert.NotNull(session);
        Assert.Equal("conv-1", session["x-opencode-session"]);
        Assert.Equal("conv-1", byHost!["x-opencode-session"]);

        Assert.Null(ClientIdentityHelpers.GetOpenCodeSessionHeaders(Other, "conv-1"));
        Assert.Null(ClientIdentityHelpers.GetOpenCodeSessionHeaders(OpenCode, null));
        Assert.Null(ClientIdentityHelpers.GetOpenCodeSessionHeaders(OpenCode, ""));
    }

    [Fact]
    public void Pi_Attribution_Matches_Pi()
    {
        var attribution = ClientIdentityHelpers.PiAttribution;
        Assert.Equal("pi", attribution.Name);
        Assert.Equal("https://pi.dev", attribution.Url);
        Assert.Equal(new[] { "cli-agent" }, attribution.Categories);
    }
}
