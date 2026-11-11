using System;
using System.Collections.Generic;
using PiUnityAgent.Ai;

namespace PiUnityAgent.Ai.Tests;

public class SessionResourcesTests
{
    [Fact]
    public void Cleanup_Invokes_All_Registered_Callbacks_With_SessionId()
    {
        var received = new List<string?>();
        var first = SessionResources.RegisterSessionResourceCleanup(id => received.Add($"first:{id}"));
        var second = SessionResources.RegisterSessionResourceCleanup(id => received.Add($"second:{id}"));
        try
        {
            SessionResources.CleanupSessionResources("s1");
            // 集合遍历顺序不契约化（pi 用 Set），只断言全部回调各收到一次正确的 sessionId。
            Assert.Equal(2, received.Count);
            Assert.Contains("first:s1", received);
            Assert.Contains("second:s1", received);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void Cleanup_Without_SessionId_Passes_Null()
    {
        string? seen = "sentinel";
        var handle = SessionResources.RegisterSessionResourceCleanup(id => seen = id);
        try
        {
            SessionResources.CleanupSessionResources();
            Assert.Null(seen);
        }
        finally
        {
            handle.Dispose();
        }
    }

    [Fact]
    public void Dispose_Unregisters_Callback()
    {
        var calls = 0;
        var handle = SessionResources.RegisterSessionResourceCleanup(_ => calls++);
        handle.Dispose();
        SessionResources.CleanupSessionResources("s1");
        Assert.Equal(0, calls);

        // 重复 Dispose 幂等（pi 的注销函数重复调用也是无害删除）。
        handle.Dispose();
        SessionResources.CleanupSessionResources("s2");
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Callback_Exceptions_Are_Aggregated_And_Do_Not_Stop_Others()
    {
        var executed = new List<string>();
        var failing = SessionResources.RegisterSessionResourceCleanup(_ =>
        {
            executed.Add("fail");
            throw new InvalidOperationException("boom");
        });
        var healthy = SessionResources.RegisterSessionResourceCleanup(id =>
        {
            executed.Add(id ?? "null");
        });
        try
        {
            var error = Assert.Throws<AggregateException>(
                () => SessionResources.CleanupSessionResources("s1"));
            Assert.Contains("Failed to cleanup session resources", error.Message);
            Assert.Single(error.InnerExceptions);
            Assert.Equal("boom", Assert.IsType<InvalidOperationException>(error.InnerExceptions[0]).Message);
            // 两个回调都被执行（遍历顺序不契约化）：失败的那个不拦截其余回调。
            Assert.Equal(2, executed.Count);
            Assert.Contains("fail", executed);
            Assert.Contains("s1", executed);
        }
        finally
        {
            failing.Dispose();
            healthy.Dispose();
        }
    }

    [Fact]
    public void Multiple_Failures_Are_All_Aggregated()
    {
        var first = SessionResources.RegisterSessionResourceCleanup(_ => throw new ArgumentException("one"));
        var second = SessionResources.RegisterSessionResourceCleanup(_ => throw new ArgumentException("two"));
        try
        {
            var error = Assert.Throws<AggregateException>(
                () => SessionResources.CleanupSessionResources());
            Assert.Equal(2, error.InnerExceptions.Count);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void Cleanup_With_No_Registrations_Is_Noop()
    {
        SessionResources.CleanupSessionResources("s1");
        SessionResources.CleanupSessionResources();
    }
}
