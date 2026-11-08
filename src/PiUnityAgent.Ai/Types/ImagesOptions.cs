namespace PiUnityAgent.Ai.Types;

/// <summary>
/// pi ImagesOptions/ClassifierOptions 的 C# 形状；StreamOptions 已承载 metadata/temperature。
/// 额外 stream-only 字段属 B/D（代价=类型面较宽；后续可拆 ProviderRequestOptions）。
/// </summary>
public sealed record ImagesOptions : StreamOptions;
