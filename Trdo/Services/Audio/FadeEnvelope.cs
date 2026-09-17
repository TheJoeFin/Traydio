using System;

namespace Trdo.Services.Audio;

/// <summary>
/// A gain envelope that always moves from wherever it currently is. Asking it to fade out while it
/// is already silent leaves it silent; asking it to fade in half-way through a fade-out turns the
/// level around from that half-way point rather than snapping anywhere first.
/// </summary>
/// <remarks>
/// NAudio's <c>FadeInOutSampleProvider</c> keeps no notion of its current level: every
/// <c>BeginFadeOut</c> restarts from full gain and every <c>BeginFadeIn</c> from zero. For static
/// that lingers silently on an open render device between bursts, that turned a harmless "stop"
/// request (a pause landing while the device was still open) into a full-volume burst that then
/// faded out - exactly the sound the request was meant to prevent. Kept free of NAudio so the
/// envelope math can be unit-tested on its own.
/// </remarks>
internal sealed class FadeEnvelope
{
    private float _level;
    private float _start;
    private float _target;
    private int _frames;
    private int _position;

    internal FadeEnvelope(bool initiallySilent)
    {
        _level = _start = _target = initiallySilent ? 0f : 1f;
    }

    /// <summary>Current gain, in [0, 1].</summary>
    internal float Level => _level;

    /// <summary>True once the level has reached its target and no fade is in progress.</summary>
    internal bool IsSettled => _level == _target;

    /// <summary>True when the envelope is at zero and staying there.</summary>
    internal bool IsSilent => _level == 0f && _target == 0f;

    /// <summary>
    /// Starts moving from the current level towards <paramref name="target"/>, arriving exactly
    /// after <paramref name="frames"/> frames. Zero or negative frames snap to the target on the
    /// next <see cref="Advance"/>. A fade already at its target is a no-op.
    /// </summary>
    internal void FadeTo(float target, int frames)
    {
        _target = Math.Clamp(target, 0f, 1f);
        _start = _level;
        _frames = Math.Max(0, frames);
        _position = 0;
    }

    /// <summary>Moves one frame along the fade and returns the level to apply to that frame.</summary>
    internal float Advance()
    {
        if (_level == _target)
            return _level;

        _position++;

        // Interpolated from the frame index rather than accumulated per frame, so the level lands
        // on the target exactly when the fade is due instead of a rounding error short of it.
        _level = _position >= _frames
            ? _target
            : _start + (_target - _start) * ((float)_position / _frames);

        return _level;
    }
}
