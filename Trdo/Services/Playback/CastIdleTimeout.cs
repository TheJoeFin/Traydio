using System;

namespace Trdo.Services.Playback;

internal sealed class CastIdleTimeout
{
    private DateTimeOffset? _idleSince;

    public bool ShouldEndCast(bool isCasting, bool isPlaybackActive, int timeoutMinutes, DateTimeOffset now)
    {
        if (!isCasting || isPlaybackActive || timeoutMinutes <= 0)
        {
            Reset();
            return false;
        }

        _idleSince ??= now;
        return now - _idleSince.Value >= TimeSpan.FromMinutes(timeoutMinutes);
    }

    public void Reset() => _idleSince = null;
}
