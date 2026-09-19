using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;

namespace Trdo.Services.Audio;

/// <summary>
/// Plays generated FM radio static while a stream is buffering, so the gap before a station comes
/// in sounds like a radio finding it rather than like the app having stopped.
/// </summary>
/// <remarks>
/// This is the only audio <em>output</em> path built on NAudio - stream playback stays on
/// <see cref="Services.Playback.IPlaybackBackend"/>. Everything here is best-effort: a machine with
/// no render device, or one whose device is held in exclusive mode, must fall back to silence
/// rather than throw, since this is decoration on top of playback and never worth failing over.
/// </remarks>
public sealed partial class RadioStaticService : IDisposable
{
    private static readonly Lazy<RadioStaticService> _instance = new(() => new RadioStaticService());
    public static RadioStaticService Instance => _instance.Value;

    // Serialises every start/stop transition. Buffering state can flap quickly, and building or
    // tearing down a WASAPI render stream concurrently with itself is a good way to leak one.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _random = new();

    private WasapiPlayer? _player;
    private ContinuousFadeSampleProvider? _fade;
    private VolumeSampleProvider? _volume;
    private DriftingToneSampleProvider? _whine;

    // Bumped on every start. A queued teardown compares the generation it captured against this
    // one and stands down if the static was re-started while its fade-out was still running.
    private int _generation;

    private volatile bool _isAudible;
    private bool _isInitialised;
    private bool _isDisposed;

    private RadioStaticService()
    {
    }

    /// <summary>
    /// True from the moment the static starts fading in until its fade-out has fully completed.
    /// </summary>
    /// <remarks>
    /// <see cref="AudioSilenceMonitorService"/> reads this to discount its own loopback capture:
    /// static is app-generated audio, and letting it count as output would make a dead stream look
    /// alive and suppress the watchdog's silence recovery.
    /// </remarks>
    public bool IsAudible => _isAudible;

    /// <summary>
    /// Subscribes to the playback and settings changes that drive the static. Safe to call twice.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialised)
            return;

        _isInitialised = true;

        RadioPlayerService.Instance.BufferingStateChanged += OnBufferingStateChanged;
        RadioPlayerService.Instance.VolumeChanged += OnVolumeChanged;
        RadioPlayerService.Instance.PlaybackPausedByUser += OnPlaybackPausedByUser;
        SettingsService.RadioStaticEnabledChanged += OnRadioStaticEnabledChanged;
    }

    private void OnBufferingStateChanged(object? sender, bool isBuffering)
    {
        LogService.Info("RadioStatic",
            $"BufferingStateChanged received: isBuffering={isBuffering}, ActiveSourceKind={RadioPlayerService.Instance.ActiveSourceKind}, " +
            $"IsRadioStaticEnabled={SettingsService.IsRadioStaticEnabled}, _isAudible={_isAudible}");

        // Static is the sound of an actual radio dial searching for a signal - white noise
        // never buffers, and a local file merely opening (which briefly reads as "buffering"
        // on some backends) has nothing to do with a signal being found.
        // While a Sonos player fetches the stream, "buffering" is the speaker connecting -
        // static out of this PC's speakers would be the wrong room, and a signal it would
        // then never hear the end of.
        if (RadioPlayerService.Instance.ActiveSourceKind != AudioSourceKind.Radio ||
            RadioPlayerService.Instance.IsCastingToSonos)
        {
            if (_isAudible)
            {
                LogService.Info("RadioStatic", "Active source is not local radio - stopping static");
                RunDetached(StopCoreAsync);
            }

            return;
        }

        if (isBuffering)
        {
            if (SettingsService.IsRadioStaticEnabled)
            {
                LogService.Info("RadioStatic", "isBuffering=true and setting enabled - starting static");
                RunDetached(StartCoreAsync);
            }
        }
        else
        {
            LogService.Info("RadioStatic", "isBuffering=false - stopping static");
            RunDetached(StopCoreAsync);
        }
    }

    /// <summary>
    /// A user pause was just decided on. Cuts a currently-ringing burst short instead of leaving
    /// it to whatever fade it was already mid-way through - see <c>RadioPlayerService.PlaybackPausedByUser</c>.
    /// </summary>
    private void OnPlaybackPausedByUser(object? sender, EventArgs e)
    {
        if (!_isAudible)
            return;

        LogService.Info("RadioStatic", "PlaybackPausedByUser - cutting the current burst short");
        RunDetached(() => StopCoreAsync(RadioStaticProfile.PauseFadeOutMs));
    }

    private void OnVolumeChanged(object? sender, double volume)
    {
        // A plain float assignment - no need to take the gate to follow the slider.
        VolumeSampleProvider? volumeProvider = _volume;
        if (volumeProvider is not null)
            volumeProvider.Volume = RadioStaticProfile.EffectiveGain(volume);
    }

    private void OnRadioStaticEnabledChanged(object? sender, EventArgs e)
    {
        // Turning the setting off should silence static that is already playing, not let the
        // current burst run to its end.
        if (!SettingsService.IsRadioStaticEnabled && _isAudible)
            RunDetached(StopCoreAsync);
    }

    /// <summary>
    /// Fades the static in, holds it, and fades it out - the Settings page "Test" button.
    /// Works regardless of whether the setting is on, since it is a preview of the setting.
    /// </summary>
    public async Task PlayTestBurstAsync(CancellationToken cancellationToken = default)
    {
        await StartCoreAsync();

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(RadioStaticProfile.TestBurstMs), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Fall through to the stop below - a cancelled preview should still fade out.
        }

        // Don't cut off static that a real buffer is relying on: if playback started buffering
        // while the preview was running, leave it playing and let the buffering state stop it.
        if (SettingsService.IsRadioStaticEnabled && RadioPlayerService.Instance.IsBuffering)
            return;

        await StopCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_isDisposed)
                return;

            _generation++;

            if (_player is null && !TryBuildGraph())
                return;

            _volume!.Volume = RadioStaticProfile.EffectiveGain(RadioPlayerService.Instance.EffectiveVolume);
            _whine?.Restart();

            // The fade always starts from the current level, so this is also the right call when
            // the static is already audible: a fade-out still in flight simply turns around.
            bool wasAlreadyAudible = _isAudible;
            _fade!.BeginFadeIn(RadioStaticProfile.RampUpMs);
            _isAudible = true;

            if (_player!.PlaybackState != PlaybackState.Playing)
                _player.Play();

            LogService.Info("RadioStatic",
                $"StartCoreAsync: fading in (generation={_generation}, wasAlreadyAudible={wasAlreadyAudible})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioStatic] Failed to start static: {ex.Message}");
            LogService.Warn("RadioStatic", $"StartCoreAsync failed: {ex.Message}");
            DisposePlayer();
            _isAudible = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task StopCoreAsync() => StopCoreAsync(RadioStaticProfile.FadeOutMs);

    private async Task StopCoreAsync(double fadeOutMs)
    {
        int generation;

        await _gate.WaitAsync();
        try
        {
            if (_player is null || _fade is null)
            {
                LogService.Info("RadioStatic", "StopCoreAsync: no player/fade graph - nothing to stop");
                return;
            }

            // The player lingers open for a while after a burst ends (see LingerThenReleaseAsync),
            // so "stop" requests keep arriving while it is silent - every buffering=false tick, and
            // the one a user pause raises. There is nothing to fade, and re-arming the fade-out
            // here is what used to make a pause play a burst of static: the previous fade provider
            // restarted every fade-out from full gain.
            if (!_isAudible)
            {
                LogService.Info("RadioStatic", "StopCoreAsync: static already silent - nothing to stop");
                return;
            }

            _fade.BeginFadeOut(fadeOutMs);
            generation = _generation;
            LogService.Info("RadioStatic", $"StopCoreAsync: fading out over {fadeOutMs}ms (generation={generation})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioStatic] Failed to fade out static: {ex.Message}");
            return;
        }
        finally
        {
            _gate.Release();
        }

        // Let the fade actually play out before declaring the static silent.
        await Task.Delay(TimeSpan.FromMilliseconds(fadeOutMs));

        await _gate.WaitAsync();
        try
        {
            // Something re-started the static while we were waiting - it owns the player now.
            if (_generation != generation)
                return;

            // The graph is rendering zeros from here, so the silence monitor sees real silence.
            _isAudible = false;
        }
        finally
        {
            _gate.Release();
        }

        // Detached deliberately: the static is silent now, so callers waiting on the stop (the
        // Settings preview re-enabling its button) must not be held up by the linger below.
        RunDetached(() => LingerThenReleaseAsync(generation));
    }

    /// <summary>
    /// Holds the render device open for a while after the static goes quiet, then releases it.
    /// </summary>
    /// <remarks>
    /// Buffering arrives in clusters - a flaky stream rebuffers repeatedly, and hopping stations
    /// buffers each time. Keeping the device means those follow-ups start fading in within a single
    /// buffer rather than re-activating WASAPI first, which is the difference between the static
    /// crossing with the station fade and arriving after it.
    /// </remarks>
    private async Task LingerThenReleaseAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(RadioStaticProfile.PlayerLingerMs));

        await _gate.WaitAsync();
        try
        {
            // Static started again while we lingered, so that burst owns the player now.
            if (_isDisposed || _generation != generation)
                return;

            DisposePlayer();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioStatic] Failed to release the static player: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds the render device and the signal graph feeding it. Returns false if no device could
    /// be opened, which is a normal outcome on a machine with no audio output.
    /// </summary>
    private bool TryBuildGraph()
    {
        WasapiPlayer? player = null;

        try
        {
            player = new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithLatency(RadioStaticProfile.OutputLatencyMs)
                .Build();

            // The sample rate is the one thing WASAPI will not convert for us, so generate at the
            // device's own rate. Bit depth and channel count the audio engine adapts on its own.
            WaveFormat mixFormat = player.DeviceMixFormat;
            int sampleRate = mixFormat.SampleRate;
            int channels = mixFormat.Channels >= 2 ? 2 : 1;

            MixingSampleProvider mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels))
            {
                // The generators never end, but ReadFully keeps the mixer from reporting silence
                // as end-of-stream if an input is ever removed.
                ReadFully = true,
            };
            DriftingToneSampleProvider whine = new(sampleRate, channels, _random);

            mixer.AddMixerInput(new FmNoiseSampleProvider(sampleRate, channels, _random));
            mixer.AddMixerInput(whine);
            mixer.AddMixerInput(new SweepingToneSampleProvider(
                sampleRate,
                channels,
                RadioStaticProfile.NextSweepLengthSeconds(_random.NextDouble()),
                RadioStaticProfile.SweepGain));

            // initiallySilent, so the very first buffer is already at zero and the ramp starts from
            // silence rather than snapping to full level.
            ContinuousFadeSampleProvider fade = new(mixer, initiallySilent: true);
            VolumeSampleProvider volume = new(fade)
            {
                Volume = RadioStaticProfile.EffectiveGain(RadioPlayerService.Instance.EffectiveVolume),
            };

            player.Init(new SampleToWaveProvider(volume));

            _player = player;
            _fade = fade;
            _volume = volume;
            _whine = whine;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioStatic] No usable audio output for static: {ex.Message}");
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
        _whine = null;

        if (player is null)
            return;

        try
        {
            player.Stop();
            player.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioStatic] Error disposing static player: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _isAudible = false;

        if (_isInitialised)
        {
            RadioPlayerService.Instance.BufferingStateChanged -= OnBufferingStateChanged;
            RadioPlayerService.Instance.VolumeChanged -= OnVolumeChanged;
            RadioPlayerService.Instance.PlaybackPausedByUser -= OnPlaybackPausedByUser;
            SettingsService.RadioStaticEnabledChanged -= OnRadioStaticEnabledChanged;
        }

        DisposePlayer();
        _gate.Dispose();
    }

    /// <summary>
    /// Runs a transition off the caller's thread. Buffering events arrive on the UI thread and
    /// building a WASAPI stream there would stall it.
    /// </summary>
    private static void RunDetached(Func<Task> action) => _ = Task.Run(async () =>
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioStatic] Unhandled error in static transition: {ex.Message}");
        }
    });
}
