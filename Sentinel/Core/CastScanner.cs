using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Sentinel.Data;
using System;
using System.Collections.Generic;
using System.Numerics;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Sentinel.Core;

public class CastScanner
{
    private readonly IObjectTable         _objectTable;
    private readonly IDataManager         _dataManager;
    private readonly Configuration        _config;
    private readonly OmenSheetReader      _omenReader;
    private readonly OmenManager           _omenManager;
    private readonly CustomOmenSpawner     _customSpawner;
    private readonly BmrShapeLoader        _bmrShapes;
    private readonly NetworkCastListener   _netListener;
    private readonly ExcludedActionLoader  _excludedActions;
    private readonly AoEResolver           _resolver;

    // Cached CastType lookups (actionId → castType)
    private readonly Dictionary<uint, byte>  _castTypeCache    = new();

    // Cached EffectRange lookups (actionId → range)
    private readonly Dictionary<uint, float> _effectRangeCache = new();

    // Active casts keyed by entity GameObjectId (populated by network CastStart events)
    private readonly Dictionary<ulong, ActiveCast> _active = new();
    private readonly List<ActiveCast>              _result = new();

    // Hook-tracked omens: keyed by VfxData* (unique per omen — avoids multi-target collisions)
    private readonly Dictionary<nint, HookTrackedOmen> _hookOmens = new();

    // Per-frame entity maps — rebuilt each Scan() call
    private readonly Dictionary<ulong, IGameObject> _entityMap  = new();
    private readonly Dictionary<nint,  IGameObject> _addressMap = new();

    /// <summary>Tracks a native game omen captured by CreateOmenDetour.</summary>
    public struct HookTrackedOmen
    {
        public nint VfxDataPtr;    // VfxData* — the omen's game object (dictionary key)
        public nint EntityAddress; // entity it's attached to (for cast bar lookup)
        public long CreationTicks;
    }

    // ── Debug-facing properties ────────────────────────────────────────────
    public int HookOmenCount   => _hookOmens.Count;
    public int ActiveCastCount => _active.Count;
    public IReadOnlyDictionary<nint, HookTrackedOmen> HookOmens => _hookOmens;

    // VfxContainer[6] offset from BattleChara base:
    //   Character (base, offset 0x0)
    //   → Vfx (VfxContainer, offset 0x1988)
    //   → _vfxData array (offset 0x18 within VfxContainer)
    //   → slot [6] (each Pointer<VfxData> is 8 bytes, so 6 * 8 = 0x30)
    //   Total: 0x1988 + 0x18 + 0x30 = 0x19D0
    private const int VfxContainerOmenOffset = 0x19D0;

    public int LastScanEntityCount { get; private set; }
    public int LastScanCastCount   { get; private set; }

    public CastScanner(IObjectTable objectTable, IDataManager dataManager,
                       Configuration config, OmenSheetReader omenReader,
                       OmenManager omenManager, CustomOmenSpawner customSpawner,
                       BmrShapeLoader bmrShapes, NetworkCastListener netListener,
                       ExcludedActionLoader excludedActions, AoEResolver resolver)
    {
        _objectTable     = objectTable;
        _dataManager     = dataManager;
        _config          = config;
        _omenReader      = omenReader;
        _omenManager     = omenManager;
        _customSpawner   = customSpawner;
        _bmrShapes       = bmrShapes;
        _netListener     = netListener;
        _excludedActions = excludedActions;
        _resolver        = resolver;
    }

    public IReadOnlyList<ActiveCast> Scan()
    {
        _result.Clear();
        _omenManager.ResetFrameStats();

        if (!_config.Enabled) return _result;

        var localPlayer = _objectTable.LocalPlayer;
        var playerPos   = localPlayer?.Position ?? Vector3.Zero;

        // ── Step 1: Drain network queues ──────────────────────────────────
        bool hasNetEvents =
            !_netListener.CastStarts.IsEmpty  ||
            !_netListener.CastCancels.IsEmpty ||
            !_netListener.ActionResolves.IsEmpty;

        bool needEntityMap = hasNetEvents || _active.Count > 0 || !_netListener.CastStarts.IsEmpty;

        if (hasNetEvents)
        {
            BuildEntityMaps();

            // Drain CastStart events
            while (_netListener.CastStarts.TryDequeue(out var ev))
            {
                if (!_entityMap.TryGetValue(ev.EntityId, out var obj)) continue;
                if (Vector3.Distance(playerPos, obj.Position) > _config.MaxRange) continue;
                if (!IsTrackedObject(obj)) continue;

                uint actionId = ev.ActionId;
                if (actionId == 0) continue;
                if (ev.CastTime <= 0 || ev.CastTime > 60f) continue;

                byte castType = GetCastType(actionId);
                if (_config.HideUnavoidable && castType == 6) continue;

                bool isGroundTargeted = GetIsGroundTargeted(actionId);
                string name           = GetActionName(actionId);
                uint omenId           = _omenReader.GetActionOmenId(actionId);

                bool isNew = !_active.ContainsKey(ev.EntityId);

                _active[ev.EntityId] = new ActiveCast(
                    ev.EntityId, actionId, name,
                    Progress:         0f,
                    HasOmen:          false,
                    OmenId:           omenId,
                    CasterPosition:   obj.Position,
                    IsGroundTargeted: isGroundTargeted,
                    CastType:         castType,
                    StartTimeTicks:   Environment.TickCount64,
                    TotalCastTime:    ev.CastTime,
                    Heading:          ev.Rotation,
                    TargetPosition:   new Vector3(ev.TargetX, ev.TargetY, ev.TargetZ),
                    DetectionSource:  "NET",
                    IndicatorType:    "NONE",
                    ShapeInfo:        "");

                if (isNew)
                {
                    string? omenPath = _omenReader.GetOmenPath(omenId);
                    Plugin.Log.Debug(
                        "[Sentinel][NET-CAST] \"{Name}\" ({Id}) from {EId:X}, omenId={OId}, " +
                        "castType={CT}, ground={G}, castTime={CT2:F2}s, omenPath=\"{OPath}\"",
                        name, actionId, ev.EntityId, omenId,
                        castType, isGroundTargeted, ev.CastTime,
                        omenPath ?? "(none)");
                    DebugLog.Add("NET-CAST",
                        $"\"{name}\" ({actionId}) from 0x{ev.EntityId:X} — CastTime={ev.CastTime:F2}s, omenId={omenId}");
                }
            }

            while (_netListener.ActionResolves.TryDequeue(out var ev))
            {
                DebugLog.Add("RESOLVE", $"Action {ev.ActionId} from 0x{ev.EntityId:X}");
                _active.Remove(ev.EntityId);
            }

            while (_netListener.CastCancels.TryDequeue(out var ev))
            {
                DebugLog.Add("CANCEL", $"Entity 0x{ev.EntityId:X} cast cancelled");
                _active.Remove(ev.EntityId);
            }
        }

        // ── Step 1.5: Drain CreateOmen hook events ────────────────────────
        // These are native game omens detected by CreateOmenDetour, independent
        // of network events. Catches helper entities, EventObj, etc.
        // Keyed by VfxData* so multiple omens from the same entity don't collide.
        while (_omenManager.OmenSpawnEvents.TryDequeue(out var hookEv))
        {
            bool added = _hookOmens.TryAdd(hookEv.VfxDataPtr, new HookTrackedOmen
            {
                VfxDataPtr    = hookEv.VfxDataPtr,
                EntityAddress = hookEv.EntityAddress,
                CreationTicks = hookEv.CreationTicks,
            });
            if (added)
                DebugLog.Add("HOOK-OMEN",
                    $"VfxData=0x{hookEv.VfxDataPtr:X} entity=0x{hookEv.EntityAddress:X} — tracking started");
        }

        // ── Step 2: Update network-tracked casts from ObjectTable ─────────
        if (_active.Count > 0)
        {
            if (_entityMap.Count == 0) BuildEntityMaps();

            var toRemove = new HashSet<ulong>(_active.Keys);
            int entityCount = 0;

            foreach (var kvp in _active)
            {
                ulong entityId = kvp.Key;

                if (!_entityMap.TryGetValue(entityId, out var obj))
                    continue;

                entityCount++;
                toRemove.Remove(entityId);

                if (obj is not IBattleChara bchara) continue;

                var cast = kvp.Value;

                float elapsed  = (Environment.TickCount64 - cast.StartTimeTicks) / 1000f;
                float progress = Math.Clamp(elapsed / cast.TotalCastTime, 0f, 1f);

                uint actionId         = cast.ActionId;
                bool isGroundTargeted = cast.IsGroundTargeted;
                uint omenId           = cast.OmenId;
                byte castType         = cast.CastType;

                // Per-frame indicator tracking — reset each frame
                string indicatorType = "NONE";
                string shapeInfo     = "";

                // ── Filtering: should this action get a custom indicator? ───────
                // Phase 1 native omen recoloring is UNAFFECTED — we always recolor
                // whatever the game already decided to show. This only gates Phase 2
                // custom VFX spawning (BMR path and Lumina fallback).
                bool excludeFromCustom = false;

                // Check 1: Explicit exclusion list (stacks, raidwides, towers, gazes, etc.)
                if (_excludedActions.IsExcluded(actionId))
                    excludeFromCustom = true;

                // Check 2: Raidwide heuristic — CT2/CT5 with EffectRange ≥ 30 are raidwides.
                // (BMR uses this same threshold; EffectRange ≥ 30 with no positional element = skip)
                if (!excludeFromCustom && (castType == 2 || castType == 5))
                {
                    float effectRange = GetEffectRange(actionId);
                    if (effectRange >= 30f)
                        excludeFromCustom = true;
                }

                // Check 3: EffectRange = 0 for non-charge CastTypes → not a real AoE.
                // CT8 (charge attacks) legitimately have EffectRange=0 with a line shape.
                if (!excludeFromCustom && castType != 8)
                {
                    float effectRange = GetEffectRange(actionId);
                    if (effectRange <= 0f)
                        excludeFromCustom = true;
                }

                if (excludeFromCustom)
                    indicatorType = "EXCLUDED";

                // Phase 1: Recolor native game omen
                bool hasOmen = false;
                unsafe
                {
                    nint addr       = obj.Address;
                    nint vfxDataPtr = *(nint*)((byte*)addr + VfxContainerOmenOffset);
                    if (vfxDataPtr != nint.Zero)
                    {
                        nint instancePtr = *(nint*)((byte*)vfxDataPtr + 0x1B8);
                        if (instancePtr != nint.Zero)
                        {
                            _omenManager.RecolorInstance(instancePtr, progress);
                            hasOmen      = true;
                            indicatorType = "NATIVE";
                            // Remove ALL hook entries for this entity — Step 3 won't double-process them.
                            // Must collect keys first since we can't Remove inside a foreach.
                            var toRemoveFromHook = new List<nint>();
                            foreach (var kv in _hookOmens)
                                if (kv.Value.EntityAddress == obj.Address)
                                    toRemoveFromHook.Add(kv.Key);
                            foreach (var hookKey in toRemoveFromHook)
                                _hookOmens.Remove(hookKey);
                        }
                    }
                }

                // Phase 2: Spawn custom omen — only if not excluded and no native omen present
                if (!hasOmen && omenId == 0 && !excludeFromCustom)
                {
                    // Compute origin and heading — shared by both BMR and Lumina fallback paths
                    float   castHeading    = cast.Heading;
                    Vector3 targetLocation = obj.Position;

                    if (isGroundTargeted)
                    {
                        var packetTarget = cast.TargetPosition;
                        if (packetTarget.X != 0f || packetTarget.Z != 0f)
                            targetLocation = packetTarget;
                        else
                        {
                            unsafe
                            {
                                nint addr = obj.Address;
                                var loc = *(Vector3*)((byte*)addr + 0x27B0);
                                if (loc.X != 0f || loc.Z != 0f)
                                    targetLocation = loc;
                            }
                        }
                    }

                    Vector3 origin = isGroundTargeted ? targetLocation : obj.Position;

                    var bmrShape = _bmrShapes.GetShape(actionId);
                    if (bmrShape != null)
                    {
                        _customSpawner.SpawnOrUpdate(
                            entityId, actionId, bmrShape,
                            origin, castHeading, obj.HitboxRadius,
                            isGroundTargeted, progress);

                        hasOmen       = true;
                        indicatorType = "CUSTOM:BMR";
                        shapeInfo     = FormatBmrShape(bmrShape);
                    }
                    else
                    {
                        // Lumina fallback: resolve shape from CastType + EffectRange
                        var shapeDef = _resolver.Resolve(actionId, origin, castHeading, obj.HitboxRadius);
                        if (shapeDef != null)
                        {
                            _customSpawner.SpawnOrUpdateFromShape(
                                entityId, actionId, shapeDef.Value, origin, progress);
                            hasOmen       = true;
                            indicatorType = "CUSTOM:LUM";
                            shapeInfo     = FormatShapeDef(shapeDef.Value);
                        }
                    }
                }

                _active[entityId] = cast with
                {
                    Progress       = progress,
                    HasOmen        = hasOmen,
                    CasterPosition = obj.Position,
                    IndicatorType  = indicatorType,
                    ShapeInfo      = shapeInfo,
                };
            }

            LastScanEntityCount = entityCount;

            foreach (var id in toRemove)
                _active.Remove(id);
        }
        else
        {
            LastScanEntityCount = 0;
        }

        // ── Step 3: Recolor hook-tracked omens not handled by Step 2 ─────
        // These are omens from helper entities or EventObj with no matching network cast.
        // Keyed by VfxData* — each omen tracked independently regardless of source entity.
        if (_hookOmens.Count > 0)
        {
            if (_addressMap.Count == 0) BuildEntityMaps();

            var hookToRemove = new List<nint>();

            foreach (var kvp in _hookOmens)
            {
                nint vfxDataPtr = kvp.Key;
                var  hookOmen   = kvp.Value;

                // Find entity in ObjectTable by address (may be null if entity despawned)
                _addressMap.TryGetValue(hookOmen.EntityAddress, out var obj);

                // Validate that VfxData* is still referenced in the entity's VfxContainer.
                // This check uses only entity memory (validated via ObjectTable) — never touches
                // VfxData itself — so it's safe even if VfxData was freed.
                bool alive = false;
                if (obj != null)
                    unsafe { alive = IsVfxDataAlive(hookOmen.EntityAddress, vfxDataPtr); }

                if (!alive)
                {
                    DebugLog.Add("HOOK-REMOVE",
                        $"VfxData=0x{vfxDataPtr:X} — entity gone or not in VfxContainer");
                    hookToRemove.Add(vfxDataPtr);
                    continue;
                }

                // Read Instance pointer from VfxData* directly (offset 0x1B8).
                // Safe because IsVfxDataAlive confirmed this VfxData* is still referenced.
                unsafe
                {
                    nint instancePtr = *(nint*)((byte*)vfxDataPtr + 0x1B8);
                    if (instancePtr != nint.Zero)
                    {
                        float progress = ComputeHookOmenProgress(obj, hookOmen);
                        _omenManager.RecolorInstance(instancePtr, progress);
                        Plugin.Log.Debug(
                            "[Sentinel][HOOK-RECOLOR] Omen VfxData=0x{V:X} entity=0x{A:X} (progress={P:F2})",
                            vfxDataPtr, hookOmen.EntityAddress, progress);
                    }
                    else
                    {
                        // Instance went null — omen is being destroyed
                        DebugLog.Add("HOOK-REMOVE", $"VfxData=0x{vfxDataPtr:X} — Instance null, omen destroyed");
                        hookToRemove.Add(vfxDataPtr);
                    }
                }
            }

            foreach (var key in hookToRemove)
                _hookOmens.Remove(key);
        }

        LastScanCastCount = _active.Count;

        foreach (var cast in _active.Values)
            _result.Add(cast);

        return _result;
    }

    private float ComputeHookOmenProgress(IGameObject? obj, HookTrackedOmen hookOmen)
    {
        const float DefaultDuration = 5f;

        // Strategy A: entity has a cast bar — use managed cast timing
        if (obj is IBattleChara bchara && bchara.IsCasting && bchara.TotalCastTime > 0)
            return Math.Clamp(bchara.CurrentCastTime / bchara.TotalCastTime, 0f, 1f);

        // Strategy B: entity is in _active from network events — use packet timing
        if (obj != null && _active.TryGetValue(obj.GameObjectId, out var netCast) && netCast.TotalCastTime > 0)
        {
            float elapsed = (Environment.TickCount64 - netCast.StartTimeTicks) / 1000f;
            return Math.Clamp(elapsed / netCast.TotalCastTime, 0f, 1f);
        }

        // Strategy C: use time since hook fired / default duration
        float elapsedSinceHook = (Environment.TickCount64 - hookOmen.CreationTicks) / 1000f;
        return Math.Clamp(elapsedSinceHook / DefaultDuration, 0f, 1f);
    }

    /// <summary>
    /// Returns true if <paramref name="vfxDataPtr"/> appears in any of the entity's
    /// VfxContainer slots (indices 0–13). Only reads entity memory validated via
    /// ObjectTable — never touches VfxData itself.
    /// </summary>
    private static unsafe bool IsVfxDataAlive(nint entityAddr, nint vfxDataPtr)
    {
        // VfxContainer._vfxData array starts at BattleChara+0x19A0
        // Each element is a Pointer<VfxData> (8 bytes). 14 slots total.
        const int VfxContainerArrayOffset = 0x19A0;
        for (int i = 0; i < 14; i++)
        {
            nint slotPtr = *(nint*)((byte*)entityAddr + VfxContainerArrayOffset + i * 8);
            if (slotPtr == vfxDataPtr) return true;
        }
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the per-frame entity maps. Called lazily — at most once per Scan().
    /// Populates both _entityMap (by GameObjectId) and _addressMap (by Address).
    /// </summary>
    private void BuildEntityMaps()
    {
        _entityMap.Clear();
        _addressMap.Clear();
        foreach (var obj in _objectTable)
        {
            if (obj.GameObjectId != 0)
                _entityMap[obj.GameObjectId] = obj;
            if (obj.Address != nint.Zero)
                _addressMap[obj.Address] = obj;
        }
    }

    // Kept for compatibility with network event code that called BuildEntityMap()
    private Dictionary<ulong, IGameObject> BuildEntityMap()
    {
        BuildEntityMaps();
        return _entityMap;
    }

    private static bool IsTrackedObject(IGameObject obj)
    {
        if (obj.ObjectKind == ObjectKind.BattleNpc)
        {
            if (obj is not IBattleNpc bnpc) return false;
            return IsHostile(bnpc);
        }
        return obj.ObjectKind == ObjectKind.EventObj;
    }

    private static bool IsHostile(IBattleNpc bnpc)
        => bnpc.BattleNpcKind == BattleNpcSubKind.Enemy;

    private byte GetCastType(uint actionId)
    {
        if (_castTypeCache.TryGetValue(actionId, out var cached))
            return cached;

        byte ct = 0;
        var sheet = _dataManager.GetExcelSheet<LuminaAction>();
        var row = sheet?.GetRowOrDefault(actionId);
        if (row.HasValue) ct = row.Value.CastType;

        _castTypeCache[actionId] = ct;
        return ct;
    }

    private float GetEffectRange(uint actionId)
    {
        if (_effectRangeCache.TryGetValue(actionId, out var cached))
            return cached;

        float range = 0f;
        var sheet = _dataManager.GetExcelSheet<LuminaAction>();
        var row = sheet?.GetRowOrDefault(actionId);
        if (row.HasValue) range = row.Value.EffectRange;

        _effectRangeCache[actionId] = range;
        return range;
    }

    private bool GetIsGroundTargeted(uint actionId)
    {
        var sheet = _dataManager.GetExcelSheet<LuminaAction>();
        var row = sheet?.GetRowOrDefault(actionId);
        return row.HasValue && row.Value.TargetArea;
    }

    private string GetActionName(uint actionId)
    {
        var sheet = _dataManager.GetExcelSheet<LuminaAction>();
        var row   = sheet?.GetRowOrDefault(actionId);
        return row.HasValue ? row.Value.Name.ToString() : $"#{actionId}";
    }

    // ── Shape-info formatters (for debug display) ─────────────────────────

    private static string FormatBmrShape(BmrShapeEntry bmr)
    {
        float front = bmr.LengthFront > 0 ? bmr.LengthFront : bmr.Radius;
        float outer = bmr.OuterRadius > 0 ? bmr.OuterRadius : bmr.Radius;
        return bmr.ShapeType switch
        {
            "Circle"      => $"Circle({bmr.Radius:F1})",
            "Cone"        => $"Cone({front:F1}, {bmr.HalfAngleDeg * 2f:F0}\u00b0)",
            "Rect"        => $"Rect({front:F1} \u00d7 {bmr.HalfWidth:F1})",
            "Donut"       => $"Donut({bmr.InnerRadius:F1}/{outer:F1})",
            "DonutSector" => $"DonutSec({bmr.InnerRadius:F1}/{outer:F1})",
            "Cross"       => $"Cross({front:F1} \u00d7 {bmr.HalfWidth:F1})",
            _             => bmr.ShapeType,
        };
    }

    private static string FormatShapeDef(ShapeDefinition s) => s.Type switch
    {
        ShapeType.Circle => $"Circle({s.Radius:F1})",
        ShapeType.Cone   => $"Cone({s.Range:F1}, {s.HalfAngle * 2f * 180f / MathF.PI:F0}\u00b0)",
        ShapeType.Rect   => $"Rect({s.Range:F1} \u00d7 {s.HalfWidth:F1})",
        ShapeType.Donut  => $"Donut({s.InnerRadius:F1}/{s.Radius:F1})",
        ShapeType.Cross  => $"Cross({s.Range:F1} \u00d7 {s.HalfWidth:F1})",
        _                => s.Type.ToString(),
    };
}
