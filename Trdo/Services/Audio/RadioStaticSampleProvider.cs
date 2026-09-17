using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;

namespace Trdo.Services.Audio;

/// <summary>
/// Band-limited pink noise with a slow amplitude swell - the "shhh" bed of the radio static.
/// </summary>
/// <remarks>
/// Pink noise rather than white: its energy falls with frequency, which is what makes it read as
/// radio hiss instead of TV snow. The band-pass then trims the extremes, and the swell keeps the
/// result breathing rather than sitting at one flat level.
/// </remarks>
internal sealed class FmNoiseSampleProvider : ISampleProvider
{
    private readonly SignalGenerator _noise;
    private readonly BiQuadFilter[] _highPass;
    private readonly BiQuadFilter[] _lowPass;
    private readonly Random _random;
    private readonly int _channels;

    // Glide coefficient for the swell. The target moves in discrete jumps once per Read block;
    // gliding towards it per sample keeps those jumps from turning into audible crackle.
    private readonly float _swellGlide;

    private float _swell = 1f;
    private float _swellTarget = 1f;

    internal FmNoiseSampleProvider(int sampleRate, int channels, Random random)
    {
        _random = random;
        _channels = channels;

        _noise = new SignalGenerator(sampleRate, channels)
        {
            Type = SignalGeneratorType.Pink,
            Gain = 1.0,
        };

        // Filter state is per-channel: sharing one filter across an interleaved stereo buffer
        // would feed each channel's history into the other.
        _highPass = new BiQuadFilter[channels];
        _lowPass = new BiQuadFilter[channels];
        for (int channel = 0; channel < channels; channel++)
        {
            _highPass[channel] = BiQuadFilter.HighPassFilter(
                sampleRate, RadioStaticProfile.NoiseHighPassHz, RadioStaticProfile.NoiseFilterQ);
            _lowPass[channel] = BiQuadFilter.LowPassFilter(
                sampleRate, RadioStaticProfile.NoiseLowPassHz, RadioStaticProfile.NoiseFilterQ);
        }

        // Reach a new swell target over roughly 50 ms.
        _swellGlide = 1f / Math.Max(1f, sampleRate * 0.05f);

        WaveFormat = _noise.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int read = _noise.Read(buffer);

        _swellTarget = RadioStaticProfile.NextSwell(_swellTarget, _random.NextDouble());

        for (int i = 0; i < read; i++)
        {
            int channel = i % _channels;

            float sample = _highPass[channel].Transform(buffer[i]);
            sample = _lowPass[channel].Transform(sample);

            // Advance the glide once per frame rather than once per sample so the swell runs at
            // the same rate no matter how many channels the device has.
            if (channel == 0)
                _swell += (_swellTarget - _swell) * _swellGlide;

            buffer[i] = sample * _swell;
        }

        return read;
    }
}

/// <summary>
/// A sine that sweeps slowly up the band and back down again - the drifting heterodyne of a
/// neighbouring carrier sliding past the one you are tuned to.
/// </summary>
/// <remarks>
/// Hand-rolled rather than <c>SignalGeneratorType.Sweep</c>, which runs one-way and resets its
/// phase to restart. At a whisper that reset is inaudible, but at a level you can actually hear it
/// is a click every pass. Sweeping back down instead keeps both the phase and the frequency
/// continuous forever, and sounds more like tuning drift than a repeating riser.
/// </remarks>
internal sealed class SweepingToneSampleProvider : ISampleProvider
{
    private readonly int _channels;
    private readonly double _sampleRate;
    private readonly double _startLog;
    private readonly double _endLog;
    private readonly double _positionStep;
    private readonly float _gain;

    /// <summary>Position through one full up-and-down pass, in [0,1).</summary>
    private double _position;

    /// <summary>Running phase. Only ever advances, so the waveform is continuous.</summary>
    private double _phase;

    internal SweepingToneSampleProvider(int sampleRate, int channels, double sweepSeconds, float gain)
    {
        _channels = channels;
        _sampleRate = sampleRate;
        _gain = gain;
        _startLog = Math.Log(RadioStaticProfile.SweepStartHz);
        _endLog = Math.Log(RadioStaticProfile.SweepEndHz);
        _positionStep = 1.0 / Math.Max(1.0, sweepSeconds * sampleRate);

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / _channels;
        int index = 0;

        for (int frame = 0; frame < frames; frame++)
        {
            // Triangle traversal of the band, travelled in log space so the climb sounds even.
            double travel = _position < 0.5 ? _position * 2.0 : 2.0 - (_position * 2.0);
            double frequency = Math.Exp(_startLog + ((_endLog - _startLog) * travel));

            _phase += 2.0 * Math.PI * frequency / _sampleRate;
            if (_phase >= 2.0 * Math.PI)
                _phase -= 2.0 * Math.PI; // sine is periodic, so this is not a discontinuity

            float sample = (float)(_gain * Math.Sin(_phase));
            for (int channel = 0; channel < _channels; channel++)
                buffer[index++] = sample;

            _position += _positionStep;
            if (_position >= 1.0)
                _position -= 1.0;
        }

        return frames * _channels;
    }
}

/// <summary>
/// A quiet sine whose frequency wanders - the faint heterodyne whine that sits behind the hiss of
/// a radio parked between stations.
/// </summary>
internal sealed class DriftingToneSampleProvider : ISampleProvider
{
    private readonly SignalGenerator _tone;
    private readonly Random _random;
    private float _frequency;

    internal DriftingToneSampleProvider(int sampleRate, int channels, Random random)
    {
        _random = random;
        _frequency = RadioStaticProfile.StartingCarrierFrequency(random.NextDouble());
        _tone = new SignalGenerator(sampleRate, channels)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = _frequency,
            Gain = RadioStaticProfile.CarrierGain,
        };

        WaveFormat = _tone.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Re-rolls the pitch the whine sits on. Called when a burst begins on a player that was left
    /// running, so reusing the device does not cost the fresh starting note a new one would get.
    /// </summary>
    internal void Restart()
    {
        _frequency = RadioStaticProfile.StartingCarrierFrequency(_random.NextDouble());
        _tone.Frequency = _frequency;
    }

    public int Read(Span<float> buffer)
    {
        _frequency = RadioStaticProfile.NextCarrierFrequency(_frequency, _random.NextDouble());
        _tone.Frequency = _frequency;
        return _tone.Read(buffer);
    }
}
