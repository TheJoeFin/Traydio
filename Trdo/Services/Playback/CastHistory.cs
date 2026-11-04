using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Rooms in a live Sonos target, for display. Not saved: it describes how the household is
    /// grouped right now, which is stale the moment someone regroups.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Rooms { get; init; } = [];

    /// <summary>UUIDs of those rooms. Not saved, for the same reason as <see cref="Rooms"/>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> RoomUuids { get; init; } = [];
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
