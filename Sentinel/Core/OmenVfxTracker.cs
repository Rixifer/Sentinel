using System;
using System.Collections.Generic;

namespace Sentinel.Core;

/// <summary>
/// Manages custom omen VFX across frames using a double-buffer pattern.
/// Omens requested this frame survive; omens not requested are moved to a
/// fade-out dictionary and destroyed after FadeDurationMs milliseconds.
/// Modeled on Pictomancy's InterframeResourceTracker.
/// </summary>
public class OmenVfxTracker : IDisposable
{
    private Dictionary<string, OmenVfx> _prevActive = new();
    private Dictionary<string, OmenVfx> _currActive = new();

    private readonly Dictionary<string, (OmenVfx Vfx, long FadeStartTicks)> _fading = new();
    private const float FadeDurationMs = 500f; // 0.5 seconds

    /// <summary>Number of VFX currently displayed (active + fading).</summary>
    public int ActiveCount => _prevActive.Count + _fading.Count;

    public bool IsTouched(string key) => _currActive.ContainsKey(key);

    /// <summary>
    /// If an omen with this key already exists from the previous frame, moves it to current and returns true.
    /// </summary>
    public bool TryTouchExisting(string key, out OmenVfx vfx)
    {
        if (_prevActive.TryGetValue(key, out vfx!))
        {
            _prevActive.Remove(key);
            _currActive.Add(key, vfx);
            return true;
        }
        // Also rescue from fading — if a cast restarts before fade completes, reuse the VFX.
        if (_fading.TryGetValue(key, out var fading))
        {
            vfx = fading.Vfx;
            _fading.Remove(key);
            _currActive.Add(key, vfx);
            return true;
        }
        vfx = null!;
        return false;
    }

    /// <summary>Registers a newly-created omen as current-frame active.</summary>
    public void TouchNew(string key, OmenVfx vfx) => _currActive.Add(key, vfx);

    /// <summary>
    /// Call once per frame after all touches.
    /// Untouched VFX from the previous frame are moved to the fade-out queue.
    /// Fading VFX have their alpha reduced each frame; fully faded VFX are destroyed.
    /// </summary>
    public void Update()
    {
        // Move untouched prev-frame VFX to fading instead of destroying immediately.
        foreach (var kv in _prevActive)
            _fading.TryAdd(kv.Key, (kv.Value, Environment.TickCount64));
        _prevActive.Clear();

        // Tick fading VFX — reduce alpha, destroy when fully faded.
        var fadeDone = new List<string>();
        foreach (var kv in _fading)
        {
            float elapsed = Environment.TickCount64 - kv.Value.FadeStartTicks;
            float t = Math.Clamp(elapsed / FadeDurationMs, 0f, 1f);

            if (t >= 1f)
            {
                kv.Value.Vfx.Dispose();
                fadeDone.Add(kv.Key);
            }
            else
            {
                // Lerp alpha from current towards 0
                var color = kv.Value.Vfx.Color;
                color.W *= (1f - t);
                kv.Value.Vfx.UpdateColor(color);
            }
        }
        foreach (var key in fadeDone)
            _fading.Remove(key);

        // Swap buffers: _currActive (just-touched) → _prevActive for next frame.
        (_prevActive, _currActive) = (_currActive, _prevActive);
    }

    public void Dispose()
    {
        foreach (var kv in _prevActive) kv.Value.Dispose();
        foreach (var kv in _currActive) kv.Value.Dispose();
        foreach (var kv in _fading)     kv.Value.Vfx.Dispose();
        _prevActive.Clear();
        _currActive.Clear();
        _fading.Clear();
    }
}
