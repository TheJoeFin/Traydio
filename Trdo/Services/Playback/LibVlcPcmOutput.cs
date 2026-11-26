using NAudio.Wave;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Trdo.Services.Audio;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Trdo.Services.Playback;

/// <summary>
/// Local output for LibVLC that applies the player volume to the decoded samples before
/// Windows sees them.
/// </summary>
/// <remarks>
/// LibVLC's own WASAPI output sets the volume as Windows session volume, which Windows applies
/// after per-stream effects such as a headset EQ: a hot station then clipped inside the EQ even
/// at 5%. Here LibVLC hands its PCM over through the audio callbacks, <see cref="PcmGain"/>
/// scales it, and a shared-mode stream whose own volume stays at 100% plays it.
/// Casting is unaffected: with a renderer set LibVLC sends the stream there and never calls back.
/// </remarks>
internal sealed class LibVlcPcmOutput : IDisposable
{
    private const int Channels = 2;

    private readonly WasapiPlayer? _player;

    // LibVLC keeps only the native function pointers, so the delegates must stay referenced.
    private readonly VlcMediaPlayer.LibVLCAudioPlayCb _play;
    private readonly VlcMediaPlayer.LibVLCAudioPauseCb _pause;
    private readonly VlcMediaPlayer.LibVLCAudioResumeCb _resume;
    private readonly VlcMediaPlayer.LibVLCAudioFlushCb _flush;
    private readonly VlcMediaPlayer.LibVLCAudioDrainCb _drain;

    private short[] _pcm = [];
    private float[] _samples = [];
    private byte[] _bytes = [];
    private volatile float _gain = 1f;

    /// <summary>Builds the output on the default render device, or returns null when there is none.</summary>
    public static LibVlcPcmOutput? TryCreate(out string? failure)
    {
        WasapiPlayer? player = null;
        try
        {
            // Stream routing follows default-device changes but activates asynchronously; the
            // thread pool keeps the wait off any UI synchronization context.
            player = Task.Run(() => new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithLatency(100)
                .WithDefaultDeviceStreamRouting()
                .BuildAsync()).GetAwaiter().GetResult();

            failure = null;
            return new LibVlcPcmOutput(player.DeviceMixFormat.SampleRate, player);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            try
            {
                player?.Dispose();
            }
            catch
            {
                // Nothing useful to do if the half-built player will not dispose.
            }

            return null;
        }
    }

    /// <param name="player">Null leaves the samples in <see cref="Buffer"/> for the caller to read.</param>
    internal LibVlcPcmOutput(int sampleRate, WasapiPlayer? player)
    {
        SampleRate = sampleRate;
        Buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels), TimeSpan.FromSeconds(2))
        {
            DiscardOnBufferOverflow = true,
            // Silence on an underrun keeps the stream running through a network hiccup.
            ReadFully = player is not null
        };

        _player = player;
        _player?.Init(Buffer);

        _play = OnPlay;
        _pause = (_, _) => { };
        _resume = (_, _) => { };
        _flush = (_, _) => Buffer.ClearBuffer();
        _drain = _ => { };
    }

    public int SampleRate { get; }

    internal BufferedWaveProvider Buffer { get; }

    /// <summary>Player volume on the 0-2 scale the backends share.</summary>
    public double Volume
    {
        set => _gain = PcmGain.FromVolume(value);
    }

    public void Attach(VlcMediaPlayer player)
    {
        // LibVLC 3.0 amem delivers only S16N; any other format string falls back to it.
        player.SetAudioFormat("S16N", (uint)SampleRate, Channels);
        player.SetAudioCallbacks(_play, _pause, _resume, _flush, _drain);
    }

    public void Start()
    {
        if (_player is not null && _player.PlaybackState != PlaybackState.Playing)
            _player.Play();
    }

    public void Pause() => _player?.Pause();

    public void Stop()
    {
        _player?.Pause();
        Buffer.ClearBuffer();
    }

    private void OnPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        // Runs on LibVLC's audio thread: an exception escaping into native code ends the process.
        try
        {
            int floats = checked((int)count * Channels);
            if (_samples.Length < floats)
            {
                _pcm = new short[floats];
                _samples = new float[floats];
                _bytes = new byte[floats * sizeof(float)];
            }

            Marshal.Copy(samples, _pcm, 0, floats);
            PcmGain.ToFloat(_pcm.AsSpan(0, floats), _samples.AsSpan(0, floats), _gain);
            System.Buffer.BlockCopy(_samples, 0, _bytes, 0, floats * sizeof(float));
            Buffer.AddSamples(_bytes, 0, floats * sizeof(float));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibVlcPcmOutput] Dropped an audio block: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibVlcPcmOutput] Error disposing player: {ex.Message}");
        }
    }
}
