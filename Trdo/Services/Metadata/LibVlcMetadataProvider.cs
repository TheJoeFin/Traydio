using LibVLCSharp.Shared;
using System;
using System.Diagnostics;
using Trdo.Models;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Trdo.Services.Metadata;

/// <summary>
/// Reads now-playing metadata from LibVLC media meta and parsed events.
/// </summary>
public sealed partial class LibVlcMetadataProvider : IDisposable
{
    private VlcMediaPlayer? _mediaPlayer;
    private StreamMetadata _currentMetadata = StreamMetadata.Empty;
    private DateTime _lastReadUtc = DateTime.MinValue;
    private bool _hasSeenNowPlaying;

    public event EventHandler<StreamMetadata>? MetadataChanged;

    public StreamMetadata CurrentMetadata => _currentMetadata;

    public void Attach(VlcMediaPlayer mediaPlayer)
    {
        Detach(clearMetadata: false);
        _hasSeenNowPlaying = false;
        _mediaPlayer = mediaPlayer;
        _mediaPlayer.MediaChanged += OnMediaChanged;
        _mediaPlayer.Playing += OnPlaying;
        _mediaPlayer.TimeChanged += OnTimeChanged;
    }

    public void Detach(bool clearMetadata = true)
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.MediaChanged -= OnMediaChanged;
        _mediaPlayer.Playing -= OnPlaying;
        _mediaPlayer.TimeChanged -= OnTimeChanged;
        _mediaPlayer = null;

        if (clearMetadata)
        {
            UpdateMetadata(StreamMetadata.Empty);
        }
    }

    private void OnPlaying(object? sender, EventArgs e) => ReadMetadataFromMedia(force: true);

    private void OnMediaChanged(object? sender, MediaPlayerMediaChangedEventArgs e)
    {
        _hasSeenNowPlaying = false;
        ReadMetadataFromMedia(force: true);
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (DateTime.UtcNow - _lastReadUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        ReadMetadataFromMedia(force: false);
    }

    private void ReadMetadataFromMedia(bool force)
    {
        if (_mediaPlayer?.Media is null)
        {
            return;
        }

        _lastReadUtc = DateTime.UtcNow;
        Media media = _mediaPlayer.Media;

        string nowPlaying = media.Meta(MetadataType.NowPlaying)?.Trim() ?? string.Empty;
        string artist = media.Meta(MetadataType.Artist)?.Trim() ?? string.Empty;
        string title = media.Meta(MetadataType.Title)?.Trim() ?? string.Empty;

        StreamMetadata metadata = new()
        {
            AlbumArtUrl = media.Meta(MetadataType.ArtworkURL)
        };

        if (!string.IsNullOrWhiteSpace(nowPlaying))
        {
            // For radio streams NowPlaying carries the ICY StreamTitle ("Artist - Title").
            _hasSeenNowPlaying = true;
            metadata.StreamTitle = nowPlaying;
            StreamMetadataService.ParseArtistAndTitle(metadata);
        }
        else if (_hasSeenNowPlaying)
        {
            // LibVLC transiently reports NowPlaying as null between meta updates;
            // keep the current track instead of downgrading to the station name.
            return;
        }
        else if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            metadata.Artist = artist;
            metadata.Title = title;
            metadata.StreamTitle = $"{artist} - {title}";
        }
        else
        {
            // A lone Title meta is the station name (icy-name) or the stream URL,
            // not now-playing info — don't publish it as the current track.
            return;
        }

        UpdateMetadata(metadata);
    }

    private void UpdateMetadata(StreamMetadata metadata)
    {
        if (_currentMetadata.StreamTitle == metadata.StreamTitle &&
            _currentMetadata.Artist == metadata.Artist &&
            _currentMetadata.Title == metadata.Title &&
            _currentMetadata.AlbumArtUrl == metadata.AlbumArtUrl)
        {
            return;
        }

        _currentMetadata = metadata;
        string artStatus = string.IsNullOrWhiteSpace(metadata.AlbumArtUrl) ? "none" : metadata.AlbumArtUrl;
        LogService.Info("LibVlcMetadataProvider", $"Metadata updated: {metadata.DisplayText} (art={artStatus})");
        Debug.WriteLine($"[LibVlcMetadataProvider] Metadata updated: {metadata.DisplayText}");
        MetadataChanged?.Invoke(this, metadata);
    }

    public void Dispose()
    {
        Detach();
    }

    public void Refresh()
    {
        ReadMetadataFromMedia(force: true);
    }
}
