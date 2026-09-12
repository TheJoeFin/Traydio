using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Trdo.Models;
using Trdo.Services.Playback;
using Windows.Media.Playback;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Trdo.Services.Metadata;

/// <summary>
/// Routes metadata extraction to ICY polling, native timed metadata, or LibVLC meta based on stream/backend.
/// </summary>
public sealed partial class StreamMetadataOrchestrator : IDisposable
{
    private readonly StreamMetadataService _icyMetadataService;
    private readonly NativeTimedMetadataService _nativeTimedMetadataService;
    private readonly LibVlcMetadataProvider _libVlcMetadataProvider;
    private readonly HlsSegmentMetadataService _hlsSegmentMetadataService;
    private StreamMetadata _currentMetadata = StreamMetadata.Empty;
    private bool _providersRunning;
    private string? _runningStreamUrl;
    private PlaybackBackendKind _runningBackend;

    public StreamMetadataOrchestrator(
        StreamMetadataService icyMetadataService,
        NativeTimedMetadataService nativeTimedMetadataService,
        LibVlcMetadataProvider libVlcMetadataProvider,
        HlsSegmentMetadataService hlsSegmentMetadataService)
    {
        _icyMetadataService = icyMetadataService;
        _nativeTimedMetadataService = nativeTimedMetadataService;
        _libVlcMetadataProvider = libVlcMetadataProvider;
        _hlsSegmentMetadataService = hlsSegmentMetadataService;

        _icyMetadataService.MetadataChanged += (_, metadata) => UpdateMetadata(metadata, MetadataSource.Icy);
        _nativeTimedMetadataService.MetadataChanged += (_, metadata) => UpdateMetadata(metadata, MetadataSource.NativeTimed);
        _libVlcMetadataProvider.MetadataChanged += (_, metadata) => UpdateMetadata(metadata, MetadataSource.LibVlc);
        _hlsSegmentMetadataService.MetadataChanged += (_, metadata) => UpdateMetadata(metadata, MetadataSource.HlsSegment);
    }

    private enum MetadataSource
    {
        Icy,
        NativeTimed,
        LibVlc,
        HlsSegment
    }

    public event EventHandler<StreamMetadata>? MetadataChanged;

    public StreamMetadata CurrentMetadata => _currentMetadata;

    public void EnsureForPlayback(
        string streamUrl,
        PlaybackBackendKind backend,
        MediaPlaybackItem? playbackItem,
        VlcMediaPlayer? libVlcPlayer)
    {
        if (_providersRunning &&
            string.Equals(_runningStreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase) &&
            _runningBackend == backend)
        {
            return;
        }

        StartForPlayback(streamUrl, backend, playbackItem, libVlcPlayer);
    }

    public void StartForPlayback(
        string streamUrl,
        PlaybackBackendKind backend,
        MediaPlaybackItem? playbackItem,
        VlcMediaPlayer? libVlcPlayer)
    {
        StopProviders();

        bool isHls = HlsStreamHelper.IsLikelyHlsUrl(streamUrl);
        if (isHls)
        {
            _hlsSegmentMetadataService.StartPolling(streamUrl);
        }

        switch (backend)
        {
            case PlaybackBackendKind.Native when isHls:
                _nativeTimedMetadataService.Attach(playbackItem);
                StartIcyPollingForStream(streamUrl);
                Debug.WriteLine("[StreamMetadataOrchestrator] Using native timed metadata + HLS segment polling for HLS");
                break;
            case PlaybackBackendKind.Native:
                StartIcyPollingForStream(streamUrl);
                Debug.WriteLine("[StreamMetadataOrchestrator] Using ICY metadata polling");
                break;
            case PlaybackBackendKind.LibVlc when libVlcPlayer is not null:
                _libVlcMetadataProvider.Attach(libVlcPlayer);
                StartIcyPollingForStream(streamUrl);
                Debug.WriteLine("[StreamMetadataOrchestrator] Using LibVLC metadata + ICY polling");
                break;
            default:
                StartIcyPollingForStream(streamUrl);
                break;
        }

        _providersRunning = true;
        _runningStreamUrl = streamUrl;
        _runningBackend = backend;
    }

    public void StopAll()
    {
        StopProviders(clearMetadata: true);
    }

    private void StopProviders(bool clearMetadata = false)
    {
        _icyMetadataService.StopPolling(clearMetadata);
        _nativeTimedMetadataService.Detach(clearMetadata);
        _libVlcMetadataProvider.Detach(clearMetadata);
        _hlsSegmentMetadataService.StopPolling(clearMetadata);

        if (clearMetadata)
        {
            UpdateMetadata(StreamMetadata.Empty, MetadataSource.Icy);
        }

        _providersRunning = false;
        _runningStreamUrl = null;
    }

    public async Task RefreshAsync(
        string streamUrl,
        PlaybackBackendKind backend,
        MediaPlaybackItem? playbackItem,
        VlcMediaPlayer? libVlcPlayer,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return;
        }

        if (HlsStreamHelper.IsLikelyHlsUrl(streamUrl))
        {
            await _hlsSegmentMetadataService.RefreshAsync(streamUrl, cancellationToken);
        }

        switch (backend)
        {
            case PlaybackBackendKind.Native when HlsStreamHelper.IsLikelyHlsUrl(streamUrl):
                string? directUrl = GetIcyPollingUrl(streamUrl);
                if (!string.IsNullOrWhiteSpace(directUrl))
                {
                    Debug.WriteLine($"[StreamMetadataOrchestrator] Refresh via ICY fallback: {directUrl}");
                    await _icyMetadataService.RefreshAsync(directUrl, cancellationToken);
                }

                break;
            case PlaybackBackendKind.Native:
                await _icyMetadataService.RefreshAsync(GetIcyPollingUrl(streamUrl) ?? streamUrl, cancellationToken);
                break;
            case PlaybackBackendKind.LibVlc when libVlcPlayer is not null:
                _libVlcMetadataProvider.Refresh();
                break;
            default:
                await _icyMetadataService.RefreshAsync(GetIcyPollingUrl(streamUrl) ?? streamUrl, cancellationToken);
                break;
        }
    }

    private void StartIcyPollingForStream(string streamUrl)
    {
        string? pollingUrl = GetIcyPollingUrl(streamUrl);
        if (string.IsNullOrWhiteSpace(pollingUrl))
        {
            return;
        }

        _icyMetadataService.StartPolling(pollingUrl);
    }

    private static string? GetIcyPollingUrl(string streamUrl)
    {
        if (HlsStreamHelper.IsLikelyHlsUrl(streamUrl))
        {
            return HlsStreamHelper.GetDirectStreamUrlFromHls(streamUrl);
        }

        return streamUrl;
    }

    private void UpdateMetadata(StreamMetadata metadata, MetadataSource source)
    {
        if (!metadata.HasMetadata &&
            _currentMetadata.HasMetadata &&
            source != MetadataSource.Icy &&
            source != MetadataSource.HlsSegment)
        {
            return;
        }

        bool sameTrack = _currentMetadata.StreamTitle == metadata.StreamTitle &&
            _currentMetadata.Artist == metadata.Artist &&
            _currentMetadata.Title == metadata.Title;

        if (sameTrack &&
            string.IsNullOrWhiteSpace(metadata.AlbumArtUrl) &&
            !string.IsNullOrWhiteSpace(_currentMetadata.AlbumArtUrl))
        {
            // A source without artwork (e.g. LibVLC's own meta, which never sees ICY-embedded
            // art) shouldn't blank out artwork another source already found for this same track.
            LogService.Info("StreamMetadata", $"{source} has no art for '{metadata.DisplayText}'; keeping existing art from another source: {_currentMetadata.AlbumArtUrl}");
            metadata.AlbumArtUrl = _currentMetadata.AlbumArtUrl;
        }

        if (sameTrack && _currentMetadata.AlbumArtUrl == metadata.AlbumArtUrl)
        {
            // Worth recording: this is the point where a repeat is dropped, so a track that
            // was seen once but never announced will never be offered again.
            LogService.Info("StreamMetadata", $"{source} repeated '{metadata.DisplayText}'; ignoring");
            return;
        }

        _currentMetadata = metadata;
        string artStatus = string.IsNullOrWhiteSpace(metadata.AlbumArtUrl) ? "none" : metadata.AlbumArtUrl;
        LogService.Info("StreamMetadata", $"Updated via {source}: '{metadata.DisplayText}' (art={artStatus})");
        Debug.WriteLine($"[StreamMetadataOrchestrator] Metadata updated via {source}: {metadata.DisplayText}");
        MetadataChanged?.Invoke(this, metadata);
    }

    public void Dispose()
    {
        StopAll();
    }
}
