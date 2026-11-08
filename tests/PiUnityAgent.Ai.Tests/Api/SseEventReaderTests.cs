using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PiUnityAgent.Ai.Api;
using Xunit;

namespace PiUnityAgent.Ai.Tests.Api;

public class SseEventReaderTests
{
    [Fact]
    public async Task SseReader_Parses_Event_Data_And_Multiline()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "event: response.output_text.delta\n" +
            "data: {\"delta\":\"a\"}\n" +
            "data: {\"delta\":\"b\"}\n\n" +
            "data: [DONE]\n\n");
        await using var stream = new MemoryStream(bytes);

        var events = new List<SseEvent>();
        await foreach (var evt in SseEventReader.ReadAsync(stream, CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal("response.output_text.delta", events[0].Event);
        Assert.Equal("{\"delta\":\"a\"}\n{\"delta\":\"b\"}", events[0].Data);
        Assert.Equal("[DONE]", events[1].Data);
    }

    [Fact]
    public async Task SseReader_Skips_Comments_And_Captures_Id()
    {
        var bytes = Encoding.UTF8.GetBytes(
            ": keep-alive\n" +
            "id: 42\n" +
            "event: ping\n" +
            "data: x\n\n");
        await using var stream = new MemoryStream(bytes);

        var events = await ReadAll(stream);

        var evt = Assert.Single(events);
        Assert.Equal("ping", evt.Event);
        Assert.Equal("x", evt.Data);
        Assert.Equal("42", evt.Id);
    }

    [Fact]
    public async Task SseReader_Drops_Event_Without_Data()
    {
        var bytes = Encoding.UTF8.GetBytes("event: foo\n\ndata: real\n\n");
        await using var stream = new MemoryStream(bytes);

        var events = await ReadAll(stream);

        var evt = Assert.Single(events);
        Assert.Equal("real", evt.Data);
    }

    [Fact]
    public async Task SseReader_Handles_Field_Without_Colon_And_No_Space()
    {
        var bytes = Encoding.UTF8.GetBytes("data\n\n");
        await using var stream = new MemoryStream(bytes);

        var events = await ReadAll(stream);

        var evt = Assert.Single(events);
        Assert.Equal("", evt.Data);
    }

    [Fact]
    public async Task SseReader_Handles_CrLf_Line_Endings()
    {
        var bytes = Encoding.UTF8.GetBytes("data: a\r\ndata: b\r\n\r\n");
        await using var stream = new MemoryStream(bytes);

        var events = await ReadAll(stream);

        var evt = Assert.Single(events);
        Assert.Equal("a\nb", evt.Data);
    }

    [Fact]
    public async Task SseReader_Resets_Event_After_Dispatch()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "event: first\ndata: 1\n\n" +
            "data: 2\n\n");
        await using var stream = new MemoryStream(bytes);

        var events = await ReadAll(stream);

        Assert.Equal(2, events.Count);
        Assert.Equal("first", events[0].Event);
        Assert.Null(events[1].Event);
    }

    [Fact]
    public async Task SseReader_Empty_Stream_Yields_Nothing()
    {
        await using var stream = new MemoryStream(Array.Empty<byte>());

        var events = await ReadAll(stream);

        Assert.Empty(events);
    }

    [Fact]
    public async Task SseReader_Decodes_Utf8()
    {
        var bytes = Encoding.UTF8.GetBytes("data: {\"city\":\"杭州\"}\n\n");
        await using var stream = new MemoryStream(bytes);

        var events = await ReadAll(stream);

        Assert.Equal("{\"city\":\"杭州\"}", Assert.Single(events).Data);
    }

    [Fact]
    public async Task SseReader_Lets_Read_Exceptions_Propagate()
    {
        await using var stream = new ThrowingStream();

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var _ in SseEventReader.ReadAsync(stream, CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task SseReader_Honors_Cancellation_Between_Awaits()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: a\n\ndata: b\n\n"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SseEventReader.ReadAsync(stream, cts.Token))
            {
            }
        });
    }

    private static async Task<List<SseEvent>> ReadAll(Stream stream)
    {
        var events = new List<SseEvent>();
        await foreach (var evt in SseEventReader.ReadAsync(stream, CancellationToken.None))
        {
            events.Add(evt);
        }

        return events;
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException<int>(new IOException("boom"));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
