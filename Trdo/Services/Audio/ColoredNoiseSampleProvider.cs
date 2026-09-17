using NAudio.Dsp;
using NAudio.Wave;
using System;
using Trdo.Models;

namespace Trdo.Services.Audio;

/// <summary>
/// Generates the noise spectra offered on the "Add White Noise" page. NAudio's own
/// <see cref="NAudio.Wave.SampleProviders.SignalGenerator"/> only covers
/// <see cref="WhiteNoiseColor.White"/> and <see cref="WhiteNoiseColor.Pink"/>, so this provider
/// implements every color directly rather than mixing two generation strategies.
/// </summary>
/// <remarks>
/// Each channel gets fully independent generator state (its own pink/brown/violet/grey filter
/// history). Sharing one mono stream across channels would collapse the stereo image to a phantom
/// centre; independent channels sound the way real-world noise (rain, static, a fan) does.
/// </remarks>
internal sealed class ColoredNoiseSampleProvider : ISampleProvider
{
    private readonly Random _random = new();
    private readonly int _channels;
    private readonly ChannelState[] _channelState;

    public WaveFormat WaveFormat { get; }

    public WhiteNoiseColor Color { get; set; } = WhiteNoiseColor.White;

    public ColoredNoiseSampleProvider(int sampleRate, int channels)
    {
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        _channelState = new ChannelState[channels];
        for (int i = 0; i < channels; i++)
            _channelState[i] = new ChannelState(sampleRate);
    }

    public int Read(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i += _channels)
        {
            for (int channel = 0; channel < _channels; channel++)
                buffer[i + channel] = NextSample(_channelState[channel]);
        }

        return buffer.Length;
    }

    private float NextSample(ChannelState state)
    {
        float white = (float)(_random.NextDouble() * 2.0 - 1.0);

        return Color switch
        {
            WhiteNoiseColor.Pink => state.Pink.Next(white),
            WhiteNoiseColor.Brown => state.NextBrown(white),
            WhiteNoiseColor.Blue => state.NextBlue(white),
            WhiteNoiseColor.Violet => state.NextViolet(white),
            WhiteNoiseColor.Grey => state.NextGrey(white),
            _ => white
        };
    }

    /// <summary>Per-channel filter history for every noise color, so switching colors mid-stream
    /// never reads state left behind by a different one.</summary>
    private sealed class ChannelState
    {
        // Leaky-integrator gain compensation: integrating white noise shrinks its amplitude
        // hugely, so the result is scaled back up to roughly unit range afterwards.
        private const float BrownLeak = 0.02f;
        private const float BrownDivisor = 1.02f;
        private const float BrownGain = 3.5f;

        // Differencing two independent samples roughly doubles the variance, so the result is
        // scaled back down to keep loudness comparable to the other colors.
        private const float DifferenceGain = 0.5f;

        public readonly PinkNoiseFilter Pink = new();
        private readonly PinkNoiseFilter _blueSource = new();
        private readonly BiQuadFilter[] _greyFilters;

        private float _brownLast;
        private float _violetLastWhite;
        private float _blueLastPink;

        public ChannelState(int sampleRate)
        {
            // Approximates the inverse of the equal-loudness contour: human hearing is most
            // sensitive around 2-5 kHz, so that band is cut while the bass and treble extremes -
            // where the ear is least sensitive - are boosted, leaving white noise that reads as
            // equally loud across the whole spectrum instead of being dominated by its hiss.
            _greyFilters =
            [
                BiQuadFilter.LowShelf(sampleRate, 150f, 0.9f, 9f),
                BiQuadFilter.PeakingEQ(sampleRate, 3000f, 1.0f, -11f),
                BiQuadFilter.HighShelf(sampleRate, 8000f, 0.9f, 6f),
            ];
        }

        public float NextBrown(float white)
        {
            _brownLast = (_brownLast + BrownLeak * white) / BrownDivisor;
            return Math.Clamp(_brownLast * BrownGain, -1f, 1f);
        }

        public float NextViolet(float white)
        {
            float violet = (white - _violetLastWhite) * DifferenceGain;
            _violetLastWhite = white;
            return Math.Clamp(violet, -1f, 1f);
        }

        public float NextBlue(float white)
        {
            float pink = _blueSource.Next(white);
            float blue = (pink - _blueLastPink) * DifferenceGain;
            _blueLastPink = pink;
            return Math.Clamp(blue, -1f, 1f);
        }

        public float NextGrey(float white)
        {
            float sample = white;
            foreach (BiQuadFilter filter in _greyFilters)
                sample = filter.Transform(sample);

            return Math.Clamp(sample, -1f, 1f);
        }
    }

    /// <summary>
    /// Paul Kellet's refined pink noise filter: a bank of one-pole filters whose outputs are
    /// summed to approximate a -3 dB/octave roll-off from white noise input.
    /// </summary>
    private sealed class PinkNoiseFilter
    {
        private float _b0, _b1, _b2, _b3, _b4, _b5, _b6;

        public float Next(float white)
        {
            _b0 = 0.99886f * _b0 + white * 0.0555179f;
            _b1 = 0.99332f * _b1 + white * 0.0750759f;
            _b2 = 0.96900f * _b2 + white * 0.1538520f;
            _b3 = 0.86650f * _b3 + white * 0.3104856f;
            _b4 = 0.55000f * _b4 + white * 0.5329522f;
            _b5 = -0.7616f * _b5 - white * 0.0168980f;

            float pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362f;
            _b6 = white * 0.115926f;

            // Kellet's coefficients yield roughly +/-8 range; scale back to +/-1.
            return Math.Clamp(pink * 0.11f, -1f, 1f);
        }
    }
}
