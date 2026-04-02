using System;
using System.Collections.Generic;

namespace Sentinel.Core;

/// <summary>
/// Manages custom omen VFX across frames using a double-buffer pattern.
/// Omens requested this frame survive; omens not requested are destroyed.
/// Modeled on Pictomancy's InterframeResourceTracker.
/// </summary>
public class OmenVfxTracker : IDisposable
{
    private Dictionary<string, OmenVfx> _prevActive = new();
    private Dictionary<string, OmenVfx> _currActive = new();

    public int ActiveCount => _prevActive.Count;

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
        vfx = null!;
        return false;
    }

    /// <summary>Registers a newly-created omen as current-frame active.</summary>
    public void TouchNew(string key, OmenVfx vfx) => _currActive.Add(key, vfx);

    /// <summary>
    /// Call once per frame after all touches. Destroys untouched VFX from the previous frame, swaps buffers.
    /// </summary>
    public void Update()
    {
        foreach (var kv in _prevActive)
            kv.Value.Dispose();
        _prevActive.Clear();

        (_prevActive, _currActive) = (_currActive, _prevActive);
    }

    public void Dispose()
    {
        foreach (var kv in _prevActive) kv.Value.Dispose();
        foreach (var kv in _currActive) kv.Value.Dispose();
        _prevActive.Clear();
        _currActive.Clear();
    }
}
