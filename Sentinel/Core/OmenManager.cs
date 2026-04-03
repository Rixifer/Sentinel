using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using Sentinel.Structs;
using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace Sentinel.Core;

/// <summary>Fired by CreateOmenDetour for each enemy omen spawned by the game.</summary>
public struct HookOmenEvent
{
    public nint  VfxDataPtr;    // the VfxData* returned by CreateOmen (unique per omen)
    public nint  EntityAddress; // a2 parameter — the entity the omen is attached to
    public long  CreationTicks; // Environment.TickCount64 at hook time
    public float OmenRadius;    // a6 parameter — authoritative outer radius (already includes hitbox)
}

public unsafe class OmenManager : IDisposable
{
    private readonly Configuration _config;
    private readonly OmenSheetReader _omenReader;
    private bool _hookFailed;

    // Stats for debug window
    public int  LastRecolorCount { get; private set; }
    public bool IsHookActive     => !_hookFailed;

    /// <summary>
    /// Populated by CreateOmenDetour when an enemy omen is spawned.
    /// Drained by CastScanner in Framework.Update for hook-tracked gradient recoloring.
    /// </summary>
    public readonly ConcurrentQueue<HookOmenEvent> OmenSpawnEvents = new();

    private delegate VfxOmenData* CreateOmenDelegate(
        uint a1, nint a2, nint a3, float a4,
        float a5, float a6, float a7, float a8,
        char isEnemy, char a10);

    [Signature("E8 ?? ?? ?? ?? 48 89 84 FB ?? ?? ?? ?? 48 85 C0 74 53",
               DetourName = nameof(CreateOmenDetour))]
    private Hook<CreateOmenDelegate> _createOmenHook = null!;

    public OmenManager(Configuration config, IGameInteropProvider interop, OmenSheetReader omenReader)
    {
        _config = config;
        _omenReader = omenReader;

        try
        {
            interop.InitializeFromAttributes(this);
            _createOmenHook.Enable();
            Plugin.Log.Information("[Sentinel] OmenManager hook initialized.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(
                "[Sentinel] Omen hook failed: {Msg}. Recoloring still works via VfxContainer.", ex.Message);
            _hookFailed = true;
        }
    }

    /// <summary>
    /// Resets per-frame stats. Called at the start of each CastScanner.Scan().
    /// LastRecolorCount now tracks omens colored at spawn (incremented in CreateOmenDetour).
    /// </summary>
    public void ResetFrameStats()
    {
        LastRecolorCount = 0;
    }

    // ── CreateOmen Hook (light wall remap + spawn-time color write) ───────────

    private VfxOmenData* CreateOmenDetour(
        uint a1, nint a2, nint a3, float a4,
        float a5, float a6, float a7, float a8,
        char isEnemy, char a10)
    {
        uint originalA1 = a1;
        if (isEnemy == 1 && _config.IndicatorStyle != OmenStyle.Default)
        {
            if (_config.IndicatorStyle == OmenStyle.ForceNew)
            {
                var enhanced = _omenReader.GetEnhancedOmenId(a1);
                if (enhanced.HasValue)
                    a1 = enhanced.Value;
            }
            else if (_config.IndicatorStyle == OmenStyle.ForceOld)
            {
                var standard = _omenReader.GetStandardOmenId(a1);
                if (standard.HasValue)
                    a1 = standard.Value;
            }
        }

        Plugin.Log.Debug(
            "[Sentinel][OMEN] a1={A1}{Remap} a2=0x{A2:X} a5={A5:F4} a6={A6:F4} a8={A8:F4} isEnemy={IE}",
            a1, a1 != originalA1 ? $" (was {originalA1})" : "", a2, a5, a6, a8, (int)isEnemy);

        var vfx = _createOmenHook.Original(a1, a2, a3, a4, a5, a6, a7, a8, isEnemy, a10);

        if (isEnemy == 1 && vfx != null)
        {
            // Write configured color once at creation time — direct 0xA0 write bypasses
            // UpdateVfxColor's tinting pipeline, so all colors (including blue) work correctly.
            try
            {
                var instance = vfx->Instance;
                if (instance != null)
                {
                    var color = _config.OmenColor;
                    // Apply opacity to alpha channel
                    color = new Vector4(color.X, color.Y, color.Z, color.W * _config.OmenOpacity);
                    // HDR glow: RGB multiply pushes into bloom range
                    float glow = _config.GlowIntensity;
                    if (glow != 1.0f)
                        color = new Vector4(color.X * glow, color.Y * glow, color.Z * glow, color.W);
                    instance->Color = color;
                    LastRecolorCount++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("[Sentinel] CreateOmen color write failed: {Msg}", ex.Message);
            }

            // Queue for cast bar tracking and OmenRadius capture
            OmenSpawnEvents.Enqueue(new HookOmenEvent
            {
                VfxDataPtr    = (nint)vfx,
                EntityAddress = a2,
                CreationTicks = Environment.TickCount64,
                OmenRadius    = a6,
            });
            DebugLog.Add("HOOK-OMEN",
                $"VfxData=0x{((nint)vfx):X} entity=0x{a2:X} omenId={a1}");
        }
        return vfx;
    }

    public void Dispose()
    {
        if (!_hookFailed)
        {
            _createOmenHook?.Disable();
            _createOmenHook?.Dispose();
        }
    }
}
