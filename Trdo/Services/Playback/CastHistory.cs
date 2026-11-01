using System;
using System.Collections.Generic;

namespace Trdo.Services.Playback;

public enum CastProtocol
{
    Renderer,
    Sonos,
}

public sealed record CastHistoryDevice
{
    public CastProtocol Protocol { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string RendererType { get; init; } = string.Empty;
    public string? Uuid { get; init; }
    public string? Address { get; init; }
    public int Port { get; init; }
    public DateTimeOffset LastUsedUtc { get; init; }
}

public sealed class NetworkCastHistory
{
    public string NetworkKey { get; set; } = string.Empty;
    public bool ShortcutHidden { get; set; }
    public List<CastHistoryDevice> Devices { get; set; } = [];
}

public sealed class CastHistoryDocument
{
    public int Version { get; set; } = 1;
    public string Salt { get; set; } = string.Empty;
    public List<NetworkCastHistory> Networks { get; set; } = [];
}

public sealed record CastAttempt(string NetworkKey, long NetworkGeneration, CastHistoryDevice Device);

public sealed record CastHistoryRow(CastHistoryDevice Device, bool IsRemembered, bool IsAvailable);
