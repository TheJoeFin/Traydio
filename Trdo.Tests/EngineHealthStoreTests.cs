using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Trdo.Services.Playback;

namespace Trdo.Tests;

/// <summary>
/// Covers the per-station engine memory: the rules that decide which playback engine a
/// stream starts on next time. The scenarios mirror the real reports behind it - a station
/// that only plays on one engine, a station that is simply offline for a while, and a
/// station whose infrastructure changed after the app had already learned something.
/// </summary>
[TestClass]
public sealed class EngineHealthStoreTests
{
    private const string StreamUrl = "http://stream.riverwestradio.com:8000/riverwestradio";
    private const string OtherStreamUrl = "http://example.com:8000/other";

    private sealed class FakeStorage : IEngineHealthStorage
    {
        private readonly Dictionary<string, string> _values = [];

        public int WriteCount { get; private set; }

        public IReadOnlyCollection<string> Keys => [.. _values.Keys];

        public bool TryRead(string key, [NotNullWhen(true)] out string? value) =>
            _values.TryGetValue(key, out value);

        public void Write(string key, string value)
        {
            WriteCount++;
            _values[key] = value;
        }

        public void Remove(string key) => _values.Remove(key);

        public void Seed(string key, string value) => _values[key] = value;
    }

    private FakeStorage _storage = null!;
    private DateTime _now;

    private EngineHealthStore CreateStore()
    {
        _storage = new FakeStorage();
        _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new EngineHealthStore(_storage, () => _now);
    }

    private void Advance(TimeSpan amount) => _now += amount;

    [TestMethod]
    public void GetPreferred_WithNoHistory_ReturnsNull()
    {
        EngineHealthStore store = CreateStore();

        Assert.IsNull(store.GetPreferred(StreamUrl));
        Assert.IsNull(store.GetRecord(StreamUrl));
    }

    [TestMethod]
    public void RecordSuccess_PinsTheEngineThatPlayed()
    {
        EngineHealthStore store = CreateStore();

        Assert.IsTrue(store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native));

        Assert.AreEqual(PlaybackBackendKind.Native, store.GetPreferred(StreamUrl));
    }

    [TestMethod]
    public void RecordSuccess_WhenAlreadyPinnedAndClean_DoesNotRewrite()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native);
        int writesAfterFirst = _storage.WriteCount;

        Assert.IsFalse(store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native));
        Assert.AreEqual(writesAfterFirst, _storage.WriteCount);
    }

    [TestMethod]
    public void RecordSuccess_ClearsThatEnginesFailureHistory()
    {
        EngineHealthStore store = CreateStore();
        store.RecordFailure(StreamUrl, PlaybackBackendKind.LibVlc);
        store.RecordFailure(StreamUrl, PlaybackBackendKind.LibVlc);

        store.RecordSuccess(StreamUrl, PlaybackBackendKind.LibVlc);

        EngineHealthRecord? record = store.GetRecord(StreamUrl);
        Assert.IsNotNull(record);
        Assert.AreEqual(PlaybackBackendKind.LibVlc, record.Preferred);
        Assert.AreEqual(0, record.LibVlcFailures);
    }

    /// <summary>
    /// A single failure is not evidence about the engine - stations go down. Flipping the
    /// preference on the first failure would make every brief outage rewrite the memory.
    /// </summary>
    [TestMethod]
    public void RecordFailure_Once_DoesNotMoveAnEstablishedPreference()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.LibVlc);

        PlaybackBackendKind preferred = store.RecordFailure(StreamUrl, PlaybackBackendKind.LibVlc);

        Assert.AreEqual(PlaybackBackendKind.LibVlc, preferred);
        Assert.AreEqual(PlaybackBackendKind.LibVlc, store.GetPreferred(StreamUrl));
    }

    /// <summary>
    /// The RW Radio case: a station LibVLC accepts but never actually plays. After the
    /// second consecutive failure the app should stop starting there.
    /// </summary>
    [TestMethod]
    public void RecordFailure_TwiceOnTheSameEngine_MovesPreferenceToTheOther()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.LibVlc);

        store.RecordFailure(StreamUrl, PlaybackBackendKind.LibVlc);
        PlaybackBackendKind preferred = store.RecordFailure(StreamUrl, PlaybackBackendKind.LibVlc);

        Assert.AreEqual(PlaybackBackendKind.Native, preferred);
        Assert.AreEqual(PlaybackBackendKind.Native, store.GetPreferred(StreamUrl));
    }

    /// <summary>
    /// When the station itself is down, both engines fail. The preference must settle rather
    /// than flip on every attempt, or neither engine ever gets a clean run once it recovers.
    /// </summary>
    [TestMethod]
    public void RecordFailure_OnBothEnginesAlternately_DoesNotFlipEveryTime()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.LibVlc);

        store.RecordFailure(StreamUrl, PlaybackBackendKind.LibVlc);
        store.RecordFailure(StreamUrl, PlaybackBackendKind.LibVlc);
        Assert.AreEqual(PlaybackBackendKind.Native, store.GetPreferred(StreamUrl));

        // Native now fails too, but it has a better record than LibVLC, so it keeps the slot.
        store.RecordFailure(StreamUrl, PlaybackBackendKind.Native);
        Assert.AreEqual(PlaybackBackendKind.Native, store.GetPreferred(StreamUrl));

        store.RecordFailure(StreamUrl, PlaybackBackendKind.Native);
        Assert.AreEqual(PlaybackBackendKind.Native, store.GetPreferred(StreamUrl));
    }

    [TestMethod]
    public void RecordFailure_WithNoHistory_PrefersTheOtherEngineImmediately()
    {
        EngineHealthStore store = CreateStore();

        PlaybackBackendKind preferred = store.RecordFailure(StreamUrl, PlaybackBackendKind.LibVlc);

        Assert.AreEqual(PlaybackBackendKind.Native, preferred);
    }

    [TestMethod]
    public void MarkUnusable_MovesPreferenceWithoutWaitingForRepeatedFailures()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.LibVlc);

        store.MarkUnusable(StreamUrl, PlaybackBackendKind.LibVlc);

        Assert.AreEqual(PlaybackBackendKind.Native, store.GetPreferred(StreamUrl));
    }

    [TestMethod]
    public void Records_AreScopedToTheStreamUrl()
    {
        EngineHealthStore store = CreateStore();

        store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native);

        Assert.AreEqual(PlaybackBackendKind.Native, store.GetPreferred(StreamUrl));
        Assert.IsNull(store.GetPreferred(OtherStreamUrl));
    }

    [TestMethod]
    public void GetPreferred_IgnoresRecordsOlderThanTheMaximumAge()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native);

        Advance(EngineHealthStore.MaxRecordAge + TimeSpan.FromDays(1));

        Assert.IsNull(store.GetPreferred(StreamUrl));
    }

    [TestMethod]
    public void GetPreferred_KeepsRecordsInsideTheMaximumAge()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native);

        Advance(EngineHealthStore.MaxRecordAge - TimeSpan.FromDays(1));

        Assert.AreEqual(PlaybackBackendKind.Native, store.GetPreferred(StreamUrl));
    }

    [TestMethod]
    public void GetRecord_WithCorruptValue_ReturnsNullRatherThanThrowing()
    {
        EngineHealthStore store = CreateStore();
        _storage.Seed(EngineHealthStore.BuildKey(StreamUrl), "not-a-record");

        Assert.IsNull(store.GetRecord(StreamUrl));
    }

    [TestMethod]
    public void StoredKeys_DoNotContainTheStreamUrl()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native);

        foreach (string key in _storage.Keys)
        {
            StringAssert.StartsWith(key, EngineHealthStore.KeyPrefix);
            Assert.IsFalse(key.Contains("riverwestradio", StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void Clear_RemovesEveryRecordAndReportsTheCount()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native);
        store.RecordSuccess(OtherStreamUrl, PlaybackBackendKind.LibVlc);

        Assert.AreEqual(2, store.Clear());
        Assert.IsNull(store.GetPreferred(StreamUrl));
        Assert.IsNull(store.GetPreferred(OtherStreamUrl));
    }

    [TestMethod]
    public void Clear_LeavesUnrelatedSettingsAlone()
    {
        EngineHealthStore store = CreateStore();
        _storage.Seed("BufferLevel", "2");
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native);

        store.Clear();

        Assert.IsTrue(_storage.TryRead("BufferLevel", out _));
    }

    [TestMethod]
    public void Forget_RemovesOnlyTheGivenStream()
    {
        EngineHealthStore store = CreateStore();
        store.RecordSuccess(StreamUrl, PlaybackBackendKind.Native);
        store.RecordSuccess(OtherStreamUrl, PlaybackBackendKind.LibVlc);

        store.Forget(StreamUrl);

        Assert.IsNull(store.GetPreferred(StreamUrl));
        Assert.AreEqual(PlaybackBackendKind.LibVlc, store.GetPreferred(OtherStreamUrl));
    }

    /// <summary>
    /// The 1.x preference meant "native worked here", which is the wrong conclusion under
    /// the 2.0 LibVLC-first default, so those records must not survive the upgrade.
    /// </summary>
    [TestMethod]
    public void RemoveLegacyRecords_DropsPreviousVersionsPreferences()
    {
        EngineHealthStore store = CreateStore();
        _storage.Seed("PlaybackBackendPref_ABCD1234", "0");
        _storage.Seed("PlaybackBackendPref2_ABCD1234", "1");
        _storage.Seed("BufferLevel", "1");

        Assert.AreEqual(2, store.RemoveLegacyRecords());
        Assert.IsTrue(_storage.TryRead("BufferLevel", out _));
    }

    [TestMethod]
    public void SerializeThenDeserialize_RoundTripsEveryField()
    {
        EngineHealthRecord original = new()
        {
            Preferred = PlaybackBackendKind.LibVlc,
            NativeFailures = 3,
            LibVlcFailures = 1,
            UpdatedUtc = new DateTime(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc)
        };

        EngineHealthRecord? roundTripped = EngineHealthStore.Deserialize(EngineHealthStore.Serialize(original));

        Assert.IsNotNull(roundTripped);
        Assert.AreEqual(original.Preferred, roundTripped.Preferred);
        Assert.AreEqual(original.NativeFailures, roundTripped.NativeFailures);
        Assert.AreEqual(original.LibVlcFailures, roundTripped.LibVlcFailures);
        Assert.AreEqual(original.UpdatedUtc, roundTripped.UpdatedUtc);
    }

    [TestMethod]
    public void Deserialize_WithAnUnknownVersion_ReturnsNull()
    {
        Assert.IsNull(EngineHealthStore.Deserialize("9|n|0|0|1"));
    }

    [TestMethod]
    public void EmptyStreamUrl_IsHandledWithoutThrowing()
    {
        EngineHealthStore store = CreateStore();

        Assert.IsNull(store.GetPreferred(""));
        Assert.IsFalse(store.RecordSuccess("", PlaybackBackendKind.Native));
        Assert.AreEqual(PlaybackBackendKind.Native, store.RecordFailure("", PlaybackBackendKind.LibVlc));
        store.MarkUnusable("", PlaybackBackendKind.LibVlc);
        store.Forget("");
    }
}
