namespace SCSKiller.Core;

public sealed record EngineInfo(string Family, string Version, string? Fork, string GraphicsApi, bool Encrypted, string? Unsupported, bool NoRtPipelines = false, bool NoRayTracing = false, bool AftermathShaderDebug = false, bool VersionGuessed = false, bool ApiGuessed = false, string? Game = null, int? MaxAnisotropy = null, string? Note = null);
