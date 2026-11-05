namespace PiUnityAgent.Ai.Providers;

public interface IClock
{
    long UtcNowMs();
}

public sealed class SystemClock : IClock
{
    public long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
