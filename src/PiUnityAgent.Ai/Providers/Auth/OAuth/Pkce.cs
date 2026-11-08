using System;
using System.Security.Cryptography;
using System.Text;

namespace PiUnityAgent.Ai.Providers.Auth;

/// <summary>PKCE verifier/challenge 对。</summary>
public sealed record PkcePair(string Verifier, string Challenge);

/// <summary>
/// PKCE 工具。对应 pi <c>generatePKCE</c>（auth/oauth/pkce.ts）/ pi.net Pkce.cs：
/// 32 随机字节 base64url 作 verifier，SHA-256 摘要 base64url 作 challenge（S256）。
/// netstandard2.1 无 SHA256.HashData，走 SHA256.Create() 实例。
/// </summary>
public static class Pkce
{
    public static PkcePair Generate()
    {
        // netstandard2.1 无 RandomNumberGenerator.GetBytes(int)，用 Fill。
        var verifierBytes = new byte[32];
        RandomNumberGenerator.Fill(verifierBytes);
        var verifier = Base64UrlEncode(verifierBytes);

        using var sha256 = SHA256.Create();
        var challenge = Base64UrlEncode(sha256.ComputeHash(Encoding.UTF8.GetBytes(verifier)));
        return new PkcePair(verifier, challenge);
    }

    /// <summary>base64url（无填充）。对应 TS <c>base64urlEncode</c>。</summary>
    public static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
