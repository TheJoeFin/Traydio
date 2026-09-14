using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Services.Audio;

namespace Trdo.Tests;

/// <summary>
/// Covers the gain envelope under the radio static and white noise fades. The audible requirement
/// is that a fade only ever moves from the level it is currently at: a fade-out asked of something
/// already silent must stay silent (a pause used to replay a burst of static because of exactly
/// this), and a fade reversed mid-way must turn around rather than jump.
/// </summary>
[TestClass]
public sealed class FadeEnvelopeTests
{
    [TestMethod]
    public void FadeOut_WhenAlreadySilent_StaysSilent()
    {
        FadeEnvelope envelope = new(initiallySilent: true);

        envelope.FadeTo(0f, frames: 1000);

        Assert.IsTrue(envelope.IsSilent);
        for (int i = 0; i < 2000; i++)
            Assert.AreEqual(0f, envelope.Advance());
    }

    [TestMethod]
    public void FadeOut_AfterACompletedFadeOut_StaysSilent()
    {
        FadeEnvelope envelope = new(initiallySilent: false);
        envelope.FadeTo(0f, frames: 10);
        for (int i = 0; i < 10; i++)
            envelope.Advance();
        Assert.IsTrue(envelope.IsSilent);

        // The second stop request is the one a pause raises while the player lingers open.
        envelope.FadeTo(0f, frames: 10);

        Assert.IsTrue(envelope.IsSilent);
        Assert.AreEqual(0f, envelope.Advance());
    }

    [TestMethod]
    public void FadeOut_FromFull_ReachesSilenceInTheGivenFramesAndNeverRises()
    {
        FadeEnvelope envelope = new(initiallySilent: false);
        envelope.FadeTo(0f, frames: 100);

        float previous = 1f;
        for (int i = 0; i < 100; i++)
        {
            float level = envelope.Advance();
            Assert.IsTrue(level <= previous, $"Level rose at frame {i}");
            previous = level;
        }

        Assert.AreEqual(0f, envelope.Level);
        Assert.IsTrue(envelope.IsSettled);
    }

    [TestMethod]
    public void FadeIn_FromSilence_ReachesFullInTheGivenFramesAndNeverFalls()
    {
        FadeEnvelope envelope = new(initiallySilent: true);
        envelope.FadeTo(1f, frames: 100);

        float previous = 0f;
        for (int i = 0; i < 100; i++)
        {
            float level = envelope.Advance();
            Assert.IsTrue(level >= previous, $"Level fell at frame {i}");
            previous = level;
        }

        Assert.AreEqual(1f, envelope.Level);
        Assert.IsTrue(envelope.IsSettled);
    }

    [TestMethod]
    public void FadeIn_MidFadeOut_TurnsAroundFromTheCurrentLevel()
    {
        FadeEnvelope envelope = new(initiallySilent: false);
        envelope.FadeTo(0f, frames: 100);
        for (int i = 0; i < 50; i++)
            envelope.Advance();
        float midway = envelope.Level;
        Assert.IsTrue(midway is > 0.4f and < 0.6f, $"Expected roughly half-way, got {midway}");

        envelope.FadeTo(1f, frames: 100);

        // No snap to zero (what NAudio's provider does) - the very next frame is at or above
        // where the fade-out left off.
        Assert.IsTrue(envelope.Advance() >= midway);
    }

    [TestMethod]
    public void FadeOut_MidFadeIn_TurnsAroundFromTheCurrentLevel()
    {
        FadeEnvelope envelope = new(initiallySilent: true);
        envelope.FadeTo(1f, frames: 100);
        for (int i = 0; i < 50; i++)
            envelope.Advance();
        float midway = envelope.Level;

        envelope.FadeTo(0f, frames: 100);

        // No snap to full (what NAudio's provider does) - the very next frame is at or below
        // where the fade-in left off.
        Assert.IsTrue(envelope.Advance() <= midway);
    }

    [TestMethod]
    public void ShorterFade_Requested_MidFade_ArrivesWithinTheNewDuration()
    {
        // A user pause cuts a ringing burst short with a much shorter fade than the usual one.
        FadeEnvelope envelope = new(initiallySilent: false);
        envelope.FadeTo(0f, frames: 1000);
        for (int i = 0; i < 100; i++)
            envelope.Advance();

        envelope.FadeTo(0f, frames: 20);
        for (int i = 0; i < 20; i++)
            envelope.Advance();

        Assert.AreEqual(0f, envelope.Level);
    }

    [TestMethod]
    public void ZeroFrames_SnapsToTargetOnTheNextFrame()
    {
        FadeEnvelope envelope = new(initiallySilent: true);

        envelope.FadeTo(1f, frames: 0);

        Assert.AreEqual(1f, envelope.Advance());
    }

    [TestMethod]
    public void TinyDistance_OverManyFrames_StillArrivesOnTime()
    {
        // A per-frame step this small is below float resolution near 1.0; accumulating it would
        // never move the level, so the fade must be driven by the frame index instead.
        FadeEnvelope envelope = new(initiallySilent: false);
        envelope.FadeTo(0.9999999f, frames: 0);
        envelope.Advance();

        const int frames = 1_000_000;
        envelope.FadeTo(1f, frames);
        for (int i = 0; i < frames; i++)
            envelope.Advance();

        Assert.IsTrue(envelope.IsSettled, "Fade never settled");
        Assert.AreEqual(1f, envelope.Level);
    }

    [TestMethod]
    public void Advance_OnceSettled_HoldsTheTarget()
    {
        FadeEnvelope envelope = new(initiallySilent: true);
        envelope.FadeTo(1f, frames: 3);
        for (int i = 0; i < 3; i++)
            envelope.Advance();

        for (int i = 0; i < 10; i++)
            Assert.AreEqual(1f, envelope.Advance());
    }
}
