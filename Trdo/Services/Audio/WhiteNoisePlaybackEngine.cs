using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Trdo.Models;

namespace Trdo.Services.Audio;

/// <summary>
/// Plays generated white or pink noise as the actual content of a white noise "station".
/// </summary>
/// <remarks>
/// Deliberately independent of <see cref="RadioStaticService"/>. That service exists to mask a
/// stream while it buffers, and tells <see cref="AudioSilenceMonitorService"/> to discount its
/// output so a dead stream hiding behind the static still triggers recovery. A white noise
/// station is the opposite: its audio <em>is</em> the content, so it must read to the silence
/// monitor as ordinary output, which is exactly what keeping it off that discount path gives it
/// for free.
/// <para>
/// Unlike the buffering static, this engine keeps its WASAPI render device open across
/// pause/resume rather than tearing it down - a white noise station is meant to run for long,
/// uninterrupted stretches, so paying device re-activation latency on every toggle would be the
/// wrong trade.
/// </para>
/// </remarks>
internal sealed partial class WhiteNoisePlaybackEngine : IDisposable
{
    private const double FadeMs = 300;

    private WasapiPlayer? _player;
    private FadeInOutSampleProvider? _fade;
    private VolumeSampleProvider? _volume;
    private ColoredNoiseSampleProvider? _generator;
    private int _generation;
    private bool _isDisposed;

    /// <summary>True from the moment <see cref="Play"/> is called until <see cref="Stop"/> is.</summary>
    public bool IsPlaying { get; private set; }

    public void Play(WhiteNoiseColor color, double userVolume)
    {
        if (_isDisposed)
            return;

        try
        {
            if (_player is null && !TryBuildGraph())
                return;

            _generation++;
            SetColor(color);
            SetVolume(userVolume);
            _fade!.BeginFadeIn(FadeMs);
            IsPlaying = true;

            if (_player!.PlaybackState != PlaybackState.Playing)
                _player.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WhiteNoise] Failed to start: {ex.Message}");
            DisposePlayer();
            IsPlaying = false;
        }
    }

    public void Stop()
    {
        IsPlaying = false;

        if (_player is null || _fade is null)
            return;

        try
        {
            _fade.BeginFadeOut(FadeMs);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WhiteNoise] Failed to fade out: {ex.Message}");
            return;
        }

        int generation = _generation;

        // Detached: pausing the render device is just housekeeping and must not hold up the
        // caller, which for RadioPlayerService.Pause() is expected to return immediately.
        _ = Task.Run(async () =>
        {
            await Task.Delay((int)FadeMs + 50);

            // Play() was called again while the fade-out was still running - that burst owns
            // the player now.
            if (generation != _generation || IsPlaying)
                return;

            try
            {
                _player?.Pause();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhiteNoise] Failed to pause after fade-out: {ex.Message}");
            }
        });
    }

    public void SetVolume(double userVolume)
    {
        if (_volume is not null)
            _volume.Volume = (float)Math.Clamp(userVolume, 0, 2);
    }

    public void SetColor(WhiteNoiseColor color)
    {
        if (_generator is not null)
            _generator.Color = color;
    }

    private bool TryBuildGraph()
    {
        WasapiPlayer? player = null;

        try
        {
            player = new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithLatency(100)
                .Build();

            WaveFormat mixFormat = player.DeviceMixFormat;
            int sampleRate = mixFormat.SampleRate;
            int channels = mixFormat.Channels >= 2 ? 2 : 1;

            ColoredNoiseSampleProvider generator = new(sampleRate, channels);

            // initiallySilent, so the very first buffer is already at zero and the ramp starts
            // from silence rather than snapping to full level.
            FadeInOutSampleProvider fade = new(generator, initiallySilent: true);
            VolumeSampleProvider volume = new(fade) { Volume = 1f };

            player.Init(new SampleToWaveProvider(volume));

            _player = player;
            _generator = generator;
            _fade = fade;
            _volume = volume;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WhiteNoise] No usable audio output: {ex.Message}");
            try
            {
                player?.Dispose();
            }
            catch
            {
                // Nothing useful to do if the half-built player will not dispose.
            }

            return false;
        }
    }

    private void DisposePlayer()
    {
        WasapiPlayer? player = _player;
        _player = null;
        _fade = null;
        _volume = null;
        _generator = null;

        if (player is null)
            return;

        try
        {
            player.Stop();
            player.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WhiteNoise] Error disposing player: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        IsPlaying = false;
        DisposePlayer();
    }
}
