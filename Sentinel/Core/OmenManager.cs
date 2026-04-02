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
    public nint VfxDataPtr;    // the VfxData* returned by CreateOmen (unique per omen)
    public nint EntityAddress; // a2 parameter — the entity the omen is attached to
    public long CreationTicks; // Environment.TickCount64 at hook time
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
    /// </summary>
    public void ResetFrameStats()
    {
        LastRecolorCount = 0;
    }

    /// <summary>
    /// Recolors a single VFX instance. Called from inside CastScanner's ObjectTable iteration
    /// where the entity and its VFX are guaranteed live. instancePtr must be non-null.
    /// </summary>
    public void RecolorInstance(nint instancePtr, float progress)
    {
        if (!_config.Enabled) return;

        var color = ComputeProgressColor(progress);

        // Use the game's color update function to propagate our color to all emitters.
        // Note: this tints on top of the omen's inherent particle colors, so non-orange
        // presets may show some color mixing with the base orange. The default orange→red
        // preset works cleanly.
        if (VfxFunctions.UpdateVfxColor != null)
            VfxFunctions.UpdateVfxColor(instancePtr, color.X, color.Y, color.Z, color.W);
        else
            *(Vector4*)((byte*)instancePtr + 0xA0) = color;

        LastRecolorCount++;
    }

    public Vector4 ComputeProgressColor(float progress)
    {
        var s = _config.ColorStart;
        var e = _config.ColorEnd;
        float r = s.X + (e.X - s.X) * progress;
        float g = s.Y + (e.Y - s.Y) * progress;
        float b = s.Z + (e.Z - s.Z) * progress;

        // Alpha ramps from 70% to 100% of configured opacity over cast duration.
        float baseAlpha   = _config.OmenOpacity;
        float linearAlpha = baseAlpha * (0.7f + 0.3f * progress);
        float vfxAlpha    = LinearToVfxAlpha(linearAlpha);

        return new Vector4(r, g, b, vfxAlpha);
    }

    private static float LinearToVfxAlpha(float a)
    {
        const float threshold = 0.31372549019f; // 80/255
        return a <= threshold
            ? a * 3.1875f
            : 1f + (a - threshold) * 1.96f;
    }

    // ── CreateOmen Hook (light wall remap + initial color write) ─────────────

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
            // Write initial color at creation time — direct write works here because
            // the engine has not yet snapshotted the emitter state.
            try
            {
                var instance = vfx->Instance;
                if (instance != null)
                    instance->Color = Vector4.One; // Reset to white — UpdateVfxColor handles all coloring
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("[Sentinel] CreateOmen initial color write failed: {Msg}", ex.Message);
            }

            // Queue for per-frame gradient tracking via CastScanner's hook-omen loop
            OmenSpawnEvents.Enqueue(new HookOmenEvent
            {
                VfxDataPtr    = (nint)vfx,
                EntityAddress = a2,
                CreationTicks = Environment.TickCount64,
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
