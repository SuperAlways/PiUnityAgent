using System;
using System.Security.Cryptography;
using System.Threading;

namespace PiUnityAgent.Ai.Utils;

/// <summary>
/// pi uuid.ts：时间有序 UUIDv7。普通调用取当前时间并保证单调（晚到的钟不回退）；
/// 显式时间戳原样保留（follower id 用），不推进单调时钟。
/// 序列计数器首次由随机字节播种，之后每次调用 +1（与时间戳是否前进无关）。
/// </summary>
public static class Uuid
{
    /// <summary>48 位时间戳上限（pi MAX_UUID_V7_TIMESTAMP）。</summary>
    public const long MaxTimestamp = 0xFFFFFFFFFFFF;

    // pi：2^41 - 1，序列计数器上限。
    private const long MaxSequence = (1L << 41) - 1;

    private static readonly object StateLock = new();
    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

    private static long _lastOrdinaryTimestamp = -1;
    private static long? _sequence;

    /// <summary>生成 UUIDv7；timestampMs 缺省时取当前毫秒并与上次普通调用保持单调。</summary>
    public static string V7(long? timestampMs = null)
    {
        lock (StateLock)
        {
            var requestedTimestamp = timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (requestedTimestamp < 0 || requestedTimestamp > MaxTimestamp)
            {
                // pi 抛 RangeError；C# 对应越界语义是 ArgumentOutOfRangeException，文案逐字保留。
                throw new ArgumentOutOfRangeException(
                    nameof(timestampMs),
                    timestampMs,
                    $"UUIDv7 timestamp must be an integer between 0 and {MaxTimestamp}");
            }

            // pi：普通调用取 max(请求时间, 上次普通调用时间) 保证单调；显式时间戳不更新单调时钟。
            long effectiveTimestamp;
            if (timestampMs.HasValue)
            {
                effectiveTimestamp = requestedTimestamp;
            }
            else
            {
                effectiveTimestamp = Math.Max(requestedTimestamp, _lastOrdinaryTimestamp);
                _lastOrdinaryTimestamp = effectiveTimestamp;
            }

            var bytes = new byte[16];
            Random.GetBytes(bytes);

            if (_sequence is not { } sequence)
            {
                // 首次：由随机字节播种 40 位序列初值（必然小于 41 位上限）。
                sequence =
                    ((long)bytes[1] << 32) |
                    ((long)bytes[2] << 24) |
                    ((long)bytes[3] << 16) |
                    ((long)bytes[4] << 8) |
                    bytes[5];
            }
            else
            {
                if (sequence == MaxSequence)
                {
                    // pi：同一毫秒内序列用尽时抛 RangeError("UUIDv7 generator sequence exhausted")。
                    throw new InvalidOperationException("UUIDv7 generator sequence exhausted");
                }
                sequence++;
            }
            _sequence = sequence;

            // 时间戳大端写入 48 位（bytes[0..5]）。
            for (var index = 5; index >= 0; index--)
            {
                bytes[index] = (byte)((effectiveTimestamp >> ((5 - index) * 8)) & 0xff);
            }

            // 版本位 ver=7 + 序列高位；变体位 variant=10 + 序列中段；bytes[12..15] 保持随机。
            bytes[6] = (byte)(0x70 | ((sequence >> 37) & 0x0f));
            bytes[7] = (byte)((sequence >> 29) & 0xff);
            bytes[8] = (byte)(0x80 | ((sequence >> 23) & 0x3f));
            bytes[9] = (byte)((sequence >> 15) & 0xff);
            bytes[10] = (byte)((sequence >> 7) & 0xff);
            bytes[11] = (byte)((((sequence & 0x7f) << 1) & 0xff) | ((long)bytes[11] & 0x01));

            return Format(bytes);
        }
    }

    /// <summary>8-4-4-4-12 小写十六进制，等价 pi 的 hex 切片拼接。</summary>
    private static string Format(byte[] bytes)
    {
        const string Digits = "0123456789abcdef";
        var hex = new char[32];
        for (var index = 0; index < 16; index++)
        {
            hex[index * 2] = Digits[bytes[index] >> 4];
            hex[(index * 2) + 1] = Digits[bytes[index] & 0x0f];
        }
        return new string(hex, 0, 8)
            + "-"
            + new string(hex, 8, 4)
            + "-"
            + new string(hex, 12, 4)
            + "-"
            + new string(hex, 16, 4)
            + "-"
            + new string(hex, 20, 12);
    }
}
