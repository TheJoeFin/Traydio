using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using Trdo.Services.Audio;

namespace Trdo.Tests;

/// <summary>
/// Covers the envelope math behind the buffering radio static. The audible requirement is that the
/// static never arrives abruptly and never overpowers the stream it stands in for, so what matters
/// here is that gain scales with (and stays bounded by) the user's volume, and that the random-walk
/// envelopes cannot wander outside the range they are meant to breathe within - a swell that
/// escaped its bounds would click, and a carrier that did would whistle.
/// </summary>
[TestClass]
public sealed class RadioStaticProfileTests
{
    [TestMethod]
    public void EffectiveGain_IsProportionalToUserVolume()
    {
        Assert.AreEqual(0f, RadioStaticProfile.EffectiveGain(0), 1e-6f);
        Assert.AreEqual(RadioStaticProfile.StaticLevel, RadioStaticProfile.EffectiveGain(1), 1e-6f);
        Assert.AreEqual(RadioStaticProfile.StaticLevel / 2f, RadioStaticProfile.EffectiveGain(0.5), 1e-6f);
    }

    [TestMethod]
    public void EffectiveGain_ClampsVolumeToThePlayersRange()
    {
        // RadioPlayerService.Volume runs 0-2 (LibVLC allows amplification). Anything outside that
        // is a bug elsewhere, and must not turn into a burst of noise here.
        Assert.AreEqual(0f, RadioStaticProfile.EffectiveGain(-5), 1e-6f);
        Assert.AreEqual(RadioStaticProfile.EffectiveGain(2), RadioStaticProfile.EffectiveGain(99), 1e-6f);
    }

    [TestMethod]
    public void EffectiveGain_StaysWellBelowTheStreamItStandsIn()
    {
        // Static is background texture, not programme material.
        Assert.IsTrue(RadioStaticProfile.EffectiveGain(1) < 0.5f);
    }

    [TestMethod]
    public void NextSwell_StaysWithinBounds_AcrossALongRandomWalk()
    {
        Random random = new(Seed: 1234);
        float swell = 1f;

        for (int i = 0; i < 100_000; i++)
        {
            swell = RadioStaticProfile.NextSwell(swell, random.NextDouble());

            Assert.IsTrue(
                swell >= RadioStaticProfile.SwellMin && swell <= RadioStaticProfile.SwellMax,
                $"Swell escaped its range at step {i}: {swell}");
        }
    }

    [TestMethod]
    public void NextSwell_ClampsAnInputAlreadyOutOfRange()
    {
        Assert.AreEqual(RadioStaticProfile.SwellMax, RadioStaticProfile.NextSwell(10f, 1.0), 1e-6f);
        Assert.AreEqual(RadioStaticProfile.SwellMin, RadioStaticProfile.NextSwell(-10f, 0.0), 1e-6f);
    }

    [TestMethod]
    public void NextSweepLengthSeconds_StaysWithinItsRange()
    {
        Assert.AreEqual(RadioStaticProfile.SweepMinLengthSeconds, RadioStaticProfile.NextSweepLengthSeconds(0.0), 1e-9);
        Assert.AreEqual(RadioStaticProfile.SweepMaxLengthSeconds, RadioStaticProfile.NextSweepLengthSeconds(1.0), 1e-9);

        Random random = new(Seed: 99);
        for (int i = 0; i < 10_000; i++)
        {
            double length = RadioStaticProfile.NextSweepLengthSeconds(random.NextDouble());

            Assert.IsTrue(
                length >= RadioStaticProfile.SweepMinLengthSeconds && length <= RadioStaticProfile.SweepMaxLengthSeconds,
                $"Sweep length escaped its range at step {i}: {length}");
        }
    }

    [TestMethod]
    public void NextSweepLengthSeconds_ClampsAnInputOutsideZeroToOne()
    {
        Assert.AreEqual(RadioStaticProfile.SweepMinLengthSeconds, RadioStaticProfile.NextSweepLengthSeconds(-3.0), 1e-9);
        Assert.AreEqual(RadioStaticProfile.SweepMaxLengthSeconds, RadioStaticProfile.NextSweepLengthSeconds(42.0), 1e-9);
    }

    [TestMethod]
    public void StartingCarrierFrequency_SpansTheWholeBand()
    {
        Assert.AreEqual(RadioStaticProfile.CarrierMinHz, RadioStaticProfile.StartingCarrierFrequency(0.0), 1e-3f);
        Assert.AreEqual(RadioStaticProfile.CarrierMaxHz, RadioStaticProfile.StartingCarrierFrequency(1.0), 1e-3f);
    }

    [TestMethod]
    public void StartingCarrierFrequency_StaysInBand_AndVariesBetweenBursts()
    {
        Random random = new(Seed: 2024);
        HashSet<float> seen = [];

        for (int i = 0; i < 1_000; i++)
        {
            float start = RadioStaticProfile.StartingCarrierFrequency(random.NextDouble());

            Assert.IsTrue(
                start >= RadioStaticProfile.CarrierMinHz && start <= RadioStaticProfile.CarrierMaxHz,
                $"Starting frequency escaped the band at step {i}: {start}");

            seen.Add(start);
        }

        // The point of randomising it is that consecutive bursts do not open on the same note.
        Assert.IsTrue(seen.Count > 900, $"Starting frequency barely varied: {seen.Count} distinct values");
    }

    [TestMethod]
    public void StartingCarrierFrequency_ClampsAnInputOutsideZeroToOne()
    {
        Assert.AreEqual(RadioStaticProfile.CarrierMinHz, RadioStaticProfile.StartingCarrierFrequency(-2.0), 1e-3f);
        Assert.AreEqual(RadioStaticProfile.CarrierMaxHz, RadioStaticProfile.StartingCarrierFrequency(7.0), 1e-3f);
    }

    [TestMethod]
    public void NextCarrierFrequency_StaysWithinBounds_AcrossALongRandomWalk()
    {
        Random random = new(Seed: 5678);
        float frequency = RadioStaticProfile.StartingCarrierFrequency(0.5);

        for (int i = 0; i < 100_000; i++)
        {
            frequency = RadioStaticProfile.NextCarrierFrequency(frequency, random.NextDouble());

            Assert.IsTrue(
                frequency >= RadioStaticProfile.CarrierMinHz && frequency <= RadioStaticProfile.CarrierMaxHz,
                $"Carrier escaped its range at step {i}: {frequency}");
        }
    }
}
