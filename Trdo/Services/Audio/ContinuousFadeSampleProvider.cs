using NAudio.Wave;
using System;
using System.Threading;

namespace Trdo.Services.Audio;

/// <summary>
/// Fades a sample source in and out from whatever level it is currently at - see
/// <see cref="FadeEnvelope"/> for why NAudio's own fade provider is not used here.
/// </summary>
/// <remarks>
/// The source is always read, even while silent, so its generators keep advancing and a fade-in
/// resumes mid-texture rather than replaying the same opening samples.
/// </remarks>
internal sealed class ContinuousFadeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly FadeEnvelope _envelope;
    private readonly Lock _lock = new();
    private readonly int _channels;

    internal ContinuousFadeSampleProvider(ISampleProvider source, bool initiallySilent)
    {
        _source = source;
        _envelope = new FadeEnvelope(initiallySilent);
        _channels = Math.Max(1, source.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Ramps from the current level up to full over <paramref name="fadeDurationInMilliseconds"/>.</summary>
    public void BeginFadeIn(double fadeDurationInMilliseconds) => BeginFade(1f, fadeDurationInMilliseconds);

    /// <summary>
    /// Ramps from the current level down to silence over <paramref name="fadeDurationInMilliseconds"/>.
    /// Harmless when already silent.
    /// </summary>
    public void BeginFadeOut(double fadeDurationInMilliseconds) => BeginFade(0f, fadeDurationInMilliseconds);

    private void BeginFade(float target, double fadeDurationInMilliseconds)
    {
        int frames = (int)(fadeDurationInMilliseconds * WaveFormat.SampleRate / 1000.0);

        lock (_lock)
        {
            _envelope.FadeTo(target, frames);
        }
    }

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);

        lock (_lock)
        {
            if (_envelope.IsSettled)
            {
                if (_envelope.IsSilent)
                    buffer[..read].Clear();

                return read;
            }

            // One envelope step per frame, applied to every channel of that frame, so the fade runs
            // at the same rate regardless of channel count.
            for (int frameStart = 0; frameStart < read; frameStart += _channels)
            {
                float level = _envelope.Advance();
                int frameEnd = Math.Min(frameStart + _channels, read);

                for (int i = frameStart; i < frameEnd; i++)
                    buffer[i] *= level;
            }
        }

        return read;
    }
}
