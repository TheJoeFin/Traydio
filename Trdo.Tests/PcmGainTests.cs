using Microsoft.VisualStudio.TestTools.UnitTesting;
using Trdo.Services.Audio;

namespace Trdo.Tests;

/// <summary>
/// Covers the gain LibVLC audio gets in Traydio's own process. The volume has to land on the
/// samples themselves: Windows applies stream and session volume after per-stream effects such
/// as a headset EQ, so a hot station clipped inside the EQ at any slider position.
/// </summary>
[TestClass]
public sealed class PcmGainTests
{
    [TestMethod]
    public void FromVolume_IsLinear_LikeTheNativeEngine()
    {
        Assert.AreEqual(0.05f, PcmGain.FromVolume(0.05), 1e-6f);
        Assert.AreEqual(1f, PcmGain.FromVolume(1.0), 1e-6f);
    }

    [TestMethod]
    public void FromVolume_ClampsToTheSliderRange()
    {
        Assert.AreEqual(0f, PcmGain.FromVolume(-0.5));
        Assert.AreEqual(2f, PcmGain.FromVolume(3.0));
        Assert.AreEqual(0f, PcmGain.FromVolume(double.NaN));
    }

    [TestMethod]
    public void Apply_ScalesEverySample()
    {
        float[] samples = [1f, -0.5f, 0.25f, -1f];

        PcmGain.Apply(samples, 0.05f);

        CollectionAssert.AreEqual(new[] { 0.05f, -0.025f, 0.0125f, -0.05f }, samples);
    }

    [TestMethod]
    public void Apply_AtUnityGain_LeavesSamplesUntouched()
    {
        float[] samples = [0.9f, -0.3f];

        PcmGain.Apply(samples, 1f);

        CollectionAssert.AreEqual(new[] { 0.9f, -0.3f }, samples);
    }

    [TestMethod]
    public void ToFloat_ConvertsS16AndAppliesGain()
    {
        short[] source = [16384, -32768, 0, 32767];
        float[] target = new float[4];

        PcmGain.ToFloat(source, target, 0.5f);

        CollectionAssert.AreEqual(new[] { 0.25f, -0.5f, 0f, 32767f / 32768f * 0.5f }, target);
    }

    [TestMethod]
    public void Apply_AtZero_Silences()
    {
        float[] samples = [0.9f, -0.3f];

        PcmGain.Apply(samples, 0f);

        CollectionAssert.AreEqual(new[] { 0f, 0f }, samples);
    }
}
