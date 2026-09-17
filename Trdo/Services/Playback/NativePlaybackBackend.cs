using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;

namespace Trdo.Services.Playback;

/// <summary>
/// Windows MediaPlayer-based playback with MediaPlaybackItem and AdaptiveMediaSource for HLS.
/// </summary>
public sealed partial class NativePlaybackBackend : IPlaybackBackend
{
    private readonly MediaPlayer _player;
    private readonly HttpClient _httpClient;
    private MediaPlaybackItem? _currentPlaybackItem;
    private string? _currentStreamUrl;

    public NativePlaybackBackend(MediaPlayer player, HttpClient httpClient)
    {
        _player = player;
        _httpClient = httpClient;

        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
    }

    public PlaybackBackendKind Kind => PlaybackBackendKind.Native;

    public MediaPlaybackItem? CurrentPlaybackItem => _currentPlaybackItem;

    public bool IsPlaying =>
        _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public bool IsBuffering
    {
        get
        {
            try
            {
                MediaPlaybackState state = _player.PlaybackSession.PlaybackState;
                return state is MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
            }
            catch
            {
                return false;
            }
        }
    }

    public double BufferingProgress
    {
        get
        {
            try
            {
                return _player.PlaybackSession.BufferingProgress;
            }
            catch
            {
                return 0;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            try
            {
                return _player.PlaybackSession.Position;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
    }

    public TimeSpan? Duration
    {
        get
        {
            try
            {
                TimeSpan duration = _player.PlaybackSession.NaturalDuration;
                return duration > TimeSpan.Zero ? duration : null;
            }
            catch
            {
                return null;
            }
        }
    }

    // Required by IPlaybackBackend, but RadioPlayerService wires the native-backend case
    // directly to _player.PlaybackSession instead of these, so they're never raised here.
#pragma warning disable CS0067
    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<bool>? BufferingStateChanged;
#pragma warning restore CS0067
    public event EventHandler<PlaybackFailureEventArgs>? PlaybackFailed;
    public event EventHandler? PlaybackEnded;

    public IReadOnlyList<MediaTimeRange> GetBufferedRanges()
    {
        try
        {
            return _player.PlaybackSession.GetBufferedRanges();
        }
        catch
        {
            return [];
        }
    }

    // Windows MediaPlayer only supports 0.0-1.0, so amplification above 100% is
    // capped here; the LibVLC backend handles true >100% amplification.
    public void SetVolume(double volume) => _player.Volume = Math.Clamp(volume, 0, 1);

    public async Task<PlaybackPrepareResult> PrepareAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        ClearSource();
        _currentStreamUrl = streamUrl;

        (MediaPlaybackItem? item, string? error) =
            await HlsStreamHelper.CreatePlaybackItemAsync(streamUrl, _httpClient, cancellationToken);

        if (item is null)
        {
            LogService.Error("NativePlaybackBackend",
                $"Could not build a playback item for {LogService.Redact(streamUrl)}: {error}");
            return PlaybackPrepareResult.Failed(PlaybackBackendKind.Native, error ?? "Failed to create playback item");
        }

        _currentPlaybackItem = item;
        _player.Source = item;
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.RealTimePlayback = true;

        Debug.WriteLine($"[NativePlaybackBackend] Prepared source for {streamUrl}");
        return PlaybackPrepareResult.Succeeded(PlaybackBackendKind.Native);
    }

    public void Play()
    {
        _player.Play();
    }

    public void Pause()
    {
        _player.Pause();
    }

    public void Seek(TimeSpan position)
    {
        try
        {
            _player.PlaybackSession.Position = position;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NativePlaybackBackend] Seek failed: {ex.Message}");
        }
    }

    public void ClearSource()
    {
        MediaPlaybackItemHelper.DisposePlayerSource(_player.Source);
        _player.Source = null;
        _currentPlaybackItem = null;
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        Debug.WriteLine("[NativePlaybackBackend] MediaEnded");
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        string message = DescribeFailure(args);
        Debug.WriteLine($"[NativePlaybackBackend] MediaFailed: {message}");
        LogService.Error("NativePlaybackBackend",
            $"Media Foundation failed on {LogService.Redact(_currentStreamUrl)}: {message}");
        PlaybackFailed?.Invoke(this, new PlaybackFailureEventArgs(PlaybackBackendKind.Native, message, canRetryWithFallback: true));
    }

    /// <summary>
    /// Turns a Media Foundation failure into something a human can act on. The
    /// <c>ErrorMessage</c> alone is usually empty, and the enum alone says only
    /// "SourceNotSupported" - the HRESULT is what distinguishes an unreachable server
    /// from an unsupported codec, so it is always included.
    /// </summary>
    private static string DescribeFailure(MediaPlayerFailedEventArgs args)
    {
        int hresult = args.ExtendedErrorCode?.HResult ?? 0;
        string known = DescribeHResult(hresult);
        string detail = string.IsNullOrWhiteSpace(args.ErrorMessage)
            ? known
            : $"{known} ({args.ErrorMessage.Trim()})";

        return $"{args.Error} - {detail} [0x{hresult:X8}]";
    }

    private static string DescribeHResult(int hresult) => (uint)hresult switch
    {
        0xC00D36C4 => "the stream format isn't supported by Windows",
        0xC00D36B4 => "the stream data is invalid or the codec is unavailable",
        0xC00D001A => "the server couldn't be reached",
        0xC00D0026 => "the network connection was lost",
        0xC00D3E85 => "the server rejected the connection",
        0xC00D11BF => "the request timed out",
        0x80072EE7 => "the server's address couldn't be resolved",
        0x80072EFD => "the connection to the server was refused",
        0 => "no extended error code was reported",
        _ => "Windows reported an unspecified media error"
    };

    public void Dispose()
    {
        _player.MediaFailed -= OnMediaFailed;
        _player.MediaEnded -= OnMediaEnded;
        ClearSource();
    }
}
