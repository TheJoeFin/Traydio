using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Trdo.Services.Playback;

/// <summary>
/// Abstracts the key/value store the engine memory persists to, so the rules can be unit
/// tested without WinRT's <c>ApplicationData</c>.
/// </summary>
public interface IEngineHealthStorage
{
    IReadOnlyCollection<string> Keys { get; }

    bool TryRead(string key, [NotNullWhen(true)] out string? value);

    void Write(string key, string value);

    void Remove(string key);
}

/// <summary>
/// What the app has learned about how a particular stream behaves on each engine.
/// </summary>
public sealed class EngineHealthRecord
{
    /// <summary>The engine to try first, or <c>null</c> when nothing has been learned yet.</summary>
    public PlaybackBackendKind? Preferred { get; init; }

    /// <summary>Consecutive failures on the native (Windows Media Foundation) engine.</summary>
    public int NativeFailures { get; init; }

    /// <summary>Consecutive failures on the LibVLC engine.</summary>
    public int LibVlcFailures { get; init; }

    /// <summary>When this record was last written.</summary>
    public DateTime UpdatedUtc { get; init; }

    public int FailuresFor(PlaybackBackendKind backend) =>
        backend == PlaybackBackendKind.Native ? NativeFailures : LibVlcFailures;

    public override string ToString() =>
        $"preferred={Preferred?.ToString() ?? "none"}, nativeFailures={NativeFailures}, " +
        $"libVlcFailures={LibVlcFailures}, updated={UpdatedUtc:u}";
}

/// <summary>
/// Remembers, per stream, which playback engine actually works — so a station that only
/// plays on one engine starts on that engine every time instead of re-discovering the
/// failure on each play.
/// <para>
/// The rules are deliberately asymmetric. A <em>success</em> is strong evidence and pins the
/// engine outright, because a backend reaching confirmed playback proves it can handle the
/// stream. A <em>failure</em> is weaker evidence — a station can be down, or the network can
/// blip — so a failure only moves the preference once the other engine has a better record,
/// and a single later success wipes the failure count out.
/// </para>
/// <para>
/// Keys are a hash of the stream URL rather than the URL itself, so the settings store never
/// holds a readable list of what the user listens to.
/// </para>
/// </summary>
public sealed class EngineHealthStore
{
    /// <summary>Current record prefix. Bump the digit if the serialized shape changes.</summary>
    public const string KeyPrefix = "PlaybackEngineHealth1_";

    /// <summary>
    /// Prefixes written by earlier versions. Their values do not survive: the 1.x prefix
    /// meant "native worked here", which is wrong under the 2.0 LibVLC-first default, and
    /// the 2.0 prefix stored a bare engine with no failure history.
    /// </summary>
    private static readonly string[] LegacyKeyPrefixes =
    [
        "PlaybackBackendPref_",
        "PlaybackBackendPref2_"
    ];

    /// <summary>
    /// Records older than this are ignored. Stations change their infrastructure, and a
    /// year-old pin should not outlive the problem that created it.
    /// </summary>
    public static readonly TimeSpan MaxRecordAge = TimeSpan.FromDays(180);

    /// <summary>
    /// Failures on one engine needed before the preference moves to the other one. One
    /// failure is noise; two in a row on the same engine is a pattern.
    /// </summary>
    public const int FailuresBeforeSwitching = 2;

    private readonly IEngineHealthStorage _storage;
    private readonly Func<DateTime> _clock;

    public EngineHealthStore(IEngineHealthStorage storage, Func<DateTime>? clock = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Returns the engine to try first for this stream, or <c>null</c> when nothing has been
    /// learned yet (or what was learned has gone stale).
    /// </summary>
    public PlaybackBackendKind? GetPreferred(string streamUrl)
    {
        EngineHealthRecord? record = GetRecord(streamUrl);
        return record?.Preferred;
    }

    /// <summary>Reads the full record for a stream, for logging and diagnostics.</summary>
    public EngineHealthRecord? GetRecord(string streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return null;
        }

        try
        {
            if (!_storage.TryRead(BuildKey(streamUrl), out string? raw))
            {
                return null;
            }

            EngineHealthRecord? record = Deserialize(raw);
            if (record is null)
            {
                return null;
            }

            return _clock() - record.UpdatedUtc > MaxRecordAge ? null : record;
        }
        catch
        {
            // Engine memory is an optimisation; a corrupt entry must not break playback.
            return null;
        }
    }

    /// <summary>
    /// Records that <paramref name="backend"/> reached confirmed playback on this stream.
    /// Pins it as the preferred engine and clears its failure history.
    /// </summary>
    /// <returns>True when this changed what is stored.</returns>
    public bool RecordSuccess(string streamUrl, PlaybackBackendKind backend)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return false;
        }

        EngineHealthRecord? existing = GetRecord(streamUrl);
        if (existing is not null &&
            existing.Preferred == backend &&
            existing.FailuresFor(backend) == 0)
        {
            return false;
        }

        var updated = new EngineHealthRecord
        {
            Preferred = backend,
            NativeFailures = backend == PlaybackBackendKind.Native ? 0 : existing?.NativeFailures ?? 0,
            LibVlcFailures = backend == PlaybackBackendKind.LibVlc ? 0 : existing?.LibVlcFailures ?? 0,
            UpdatedUtc = _clock()
        };

        Save(streamUrl, updated);
        return true;
    }

    /// <summary>
    /// Records that <paramref name="backend"/> could not play this stream. The preference
    /// only moves to the other engine once this one has failed
    /// <see cref="FailuresBeforeSwitching"/> times and has a worse record than the other.
    /// </summary>
    /// <returns>The engine that should now be tried first.</returns>
    public PlaybackBackendKind RecordFailure(string streamUrl, PlaybackBackendKind backend)
    {
        PlaybackBackendKind other = Other(backend);

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return other;
        }

        EngineHealthRecord? existing = GetRecord(streamUrl);
        int nativeFailures = existing?.NativeFailures ?? 0;
        int libVlcFailures = existing?.LibVlcFailures ?? 0;

        if (backend == PlaybackBackendKind.Native)
        {
            nativeFailures++;
        }
        else
        {
            libVlcFailures++;
        }

        int failuresHere = backend == PlaybackBackendKind.Native ? nativeFailures : libVlcFailures;
        int failuresThere = backend == PlaybackBackendKind.Native ? libVlcFailures : nativeFailures;

        // Move the preference only when this engine has a genuinely worse record. Without the
        // comparison, two engines that both fail would flip the preference back and forth on
        // every attempt and neither would ever get a clean run.
        PlaybackBackendKind preferred =
            failuresHere >= FailuresBeforeSwitching && failuresHere > failuresThere
                ? other
                : existing?.Preferred ?? other;

        Save(streamUrl, new EngineHealthRecord
        {
            Preferred = preferred,
            NativeFailures = nativeFailures,
            LibVlcFailures = libVlcFailures,
            UpdatedUtc = _clock()
        });

        return preferred;
    }

    /// <summary>
    /// Immediately pins <paramref name="backend"/> as the engine to avoid, without waiting for
    /// <see cref="FailuresBeforeSwitching"/>. Used by the recovery ladder's backend-switch rung,
    /// which has already exhausted gentler options and knows this engine cannot play the stream.
    /// </summary>
    public void MarkUnusable(string streamUrl, PlaybackBackendKind backend)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return;
        }

        EngineHealthRecord? existing = GetRecord(streamUrl);
        int nativeFailures = existing?.NativeFailures ?? 0;
        int libVlcFailures = existing?.LibVlcFailures ?? 0;

        if (backend == PlaybackBackendKind.Native)
        {
            nativeFailures = Math.Max(nativeFailures + 1, FailuresBeforeSwitching);
        }
        else
        {
            libVlcFailures = Math.Max(libVlcFailures + 1, FailuresBeforeSwitching);
        }

        Save(streamUrl, new EngineHealthRecord
        {
            Preferred = Other(backend),
            NativeFailures = nativeFailures,
            LibVlcFailures = libVlcFailures,
            UpdatedUtc = _clock()
        });
    }

    /// <summary>Forgets everything learned about a single stream.</summary>
    public void Forget(string streamUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return;
        }

        try
        {
            _storage.Remove(BuildKey(streamUrl));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Forgets everything learned about every stream, so engine selection starts from the
    /// user's mode setting again. Exposed to the user as a reset in Settings.
    /// </summary>
    /// <returns>How many records were removed.</returns>
    public int Clear()
    {
        return RemoveKeysWithPrefixes(new[] { KeyPrefix });
    }

    /// <summary>Removes records written by earlier versions, whose meaning no longer holds.</summary>
    public int RemoveLegacyRecords()
    {
        return RemoveKeysWithPrefixes(LegacyKeyPrefixes);
    }

    private int RemoveKeysWithPrefixes(IReadOnlyList<string> prefixes)
    {
        try
        {
            List<string> matches = [];
            foreach (string key in _storage.Keys)
            {
                foreach (string prefix in prefixes)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        matches.Add(key);
                        break;
                    }
                }
            }

            foreach (string key in matches)
            {
                _storage.Remove(key);
            }

            return matches.Count;
        }
        catch
        {
            return 0;
        }
    }

    private void Save(string streamUrl, EngineHealthRecord record)
    {
        try
        {
            _storage.Write(BuildKey(streamUrl), Serialize(record));
        }
        catch
        {
            // ignore — losing the memory only costs a slower next start
        }
    }

    internal static string BuildKey(string streamUrl)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(streamUrl));
        return KeyPrefix + Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static PlaybackBackendKind Other(PlaybackBackendKind backend) =>
        backend == PlaybackBackendKind.Native ? PlaybackBackendKind.LibVlc : PlaybackBackendKind.Native;

    internal static string Serialize(EngineHealthRecord record)
    {
        char preferred = record.Preferred switch
        {
            PlaybackBackendKind.Native => 'n',
            PlaybackBackendKind.LibVlc => 'l',
            _ => '-'
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"1|{preferred}|{record.NativeFailures}|{record.LibVlcFailures}|{record.UpdatedUtc.Ticks}");
    }

    internal static EngineHealthRecord? Deserialize(string raw)
    {
        string[] parts = raw.Split('|');
        if (parts.Length != 5 || parts[0] != "1")
        {
            return null;
        }

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nativeFailures) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int libVlcFailures) ||
            !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) ||
            ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        PlaybackBackendKind? preferred = parts[1] switch
        {
            "n" => PlaybackBackendKind.Native,
            "l" => PlaybackBackendKind.LibVlc,
            _ => null
        };

        return new EngineHealthRecord
        {
            Preferred = preferred,
            NativeFailures = Math.Max(0, nativeFailures),
            LibVlcFailures = Math.Max(0, libVlcFailures),
            UpdatedUtc = new DateTime(ticks, DateTimeKind.Utc)
        };
    }
}
