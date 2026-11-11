using System;
using System.Text.RegularExpressions;
using PiUnityAgent.Ai.Utils;

namespace PiUnityAgent.Ai.Tests.Utils;

public class UuidTests
{
    // 8-4-4-4-12 小写十六进制；第三段版本位固定 7；第四段变体位限定 10x（8/9/a/b）。
    private static readonly Regex V7Format =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.Compiled);

    [Fact]
    public void Generated_Ids_Match_Uuidv7_Format()
    {
        for (var index = 0; index < 200; index++)
        {
            var id = Uuid.V7();
            Assert.Matches(V7Format, id);
            // 版本 nibble 显式断言（第 14 个字符，即第三段首位）。
            Assert.Equal('7', id[14]);
        }
    }

    [Fact]
    public void Explicit_Timestamp_Is_Encoded_In_High_48_Bits()
    {
        var id = Uuid.V7(1000);
        // 1000ms = 0x3e8，48 位大端 → 时间戳占据第一段全部与第二段（8+4 hex，中间带连字符）。
        Assert.StartsWith("00000000-03e8", id);
        Assert.Matches(V7Format, id);

        var maxId = Uuid.V7(Uuid.MaxTimestamp);
        Assert.StartsWith("ffffffff-ffff", maxId);
    }

    [Fact]
    public void Explicit_Timestamp_Is_Preserved_For_Follower_Identifiers()
    {
        // 先用普通调用把单调时钟推高，再请求极早的显式时间戳：pi 不钳位、原样保留。
        for (var index = 0; index < 5; index++)
        {
            Uuid.V7();
        }
        var follower = Uuid.V7(1);
        Assert.StartsWith("00000000-0001", follower);
    }

    [Fact]
    public void Ordinary_Calls_Are_Monotonically_Ordered()
    {
        var previous = Uuid.V7();
        for (var index = 0; index < 200; index++)
        {
            var current = Uuid.V7();
            Assert.True(string.CompareOrdinal(current, previous) > 0,
                $"expected {current} > {previous}");
            previous = current;
        }
    }

    [Fact]
    public void Repeated_Explicit_Timestamps_Still_Differ_Via_Sequence()
    {
        var first = Uuid.V7(1000);
        var second = Uuid.V7(1000);
        Assert.StartsWith("00000000-03e8", first);
        Assert.StartsWith("00000000-03e8", second);
        Assert.NotEqual(first, second);
        Assert.True(string.CompareOrdinal(second, first) > 0);
    }

    [Fact]
    public void Out_Of_Range_Timestamp_Throws()
    {
        var negative = Assert.Throws<ArgumentOutOfRangeException>(() => Uuid.V7(-1));
        Assert.Contains("UUIDv7 timestamp must be an integer between 0 and 281474976710655", negative.Message);

        var tooLarge = Assert.Throws<ArgumentOutOfRangeException>(
            () => Uuid.V7(Uuid.MaxTimestamp + 1));
        Assert.Contains("UUIDv7 timestamp must be an integer between 0 and 281474976710655", tooLarge.Message);
    }
}
