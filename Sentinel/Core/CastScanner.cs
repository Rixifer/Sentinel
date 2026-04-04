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

    // Per-frame entity map — rebuilt each Scan() call
    private readonly Dictionary<ulong, IGameObject> _entityMap = new();

    /// <summary>Tracks a native game omen captured by CreateOmenDetour.</summary>
    public struct HookTrackedOmen
    {
        public nint  VfxDataPtr;    // VfxData* — the omen's game object (dictionary key)
        public nint  EntityAddress; // entity it's attached to (for cast bar lookup)
        public long  CreationTicks;
        public float OmenRadius;    // a6 from CreateOmen — authoritative outer radius
        public uint  OmenId;        // a1 from CreateOmen — authoritative omen row ID
        public float OmenRotation;  // a5 from CreateOmen — authoritative omen direction (radians)
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
        var playerPos   = localPlayer?.Position ?? Vector3.Zero; // kept for future use

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
                    DetectionSource:      "NET",
                    IndicatorType:        "NONE",
                    ShapeInfo:            "",
                    CasterHitboxRadius:   obj.HitboxRadius,
                    OmenRadius:           null);

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
                // Only log RESOLVE for enemy entities (0x4000xxxx range) — skip player spam
                if ((ev.EntityId & 0xFF000000) == 0x40000000)
                    DebugLog.Add("RESOLVE", $"Action {ev.ActionId} from 0x{ev.EntityId:X}");

                // Don't remove immediately — force progress to 1.0 and let the omen
                // keep rendering at the end color until the game destroys the VFX.
                // Without this, the server resolves early and the gradient never reaches the end.
                if (_active.TryGetValue(ev.EntityId, out var resolved))
                    _active[ev.EntityId] = resolved with { Progress = 1.0f, ResolvedTicks = Environment.TickCount64 };
                else
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
                OmenRadius    = hookEv.OmenRadius,
                OmenId        = hookEv.OmenId,
                OmenRotation  = hookEv.OmenRotation,
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

                // Resolved casts: keep at progress=1.0 for up to 2 seconds so the cast bar
                // shows full completion. Color was set at spawn, nothing to update per-frame.
                if (cast.ResolvedTicks > 0)
                {
                    float sinceDeath = (Environment.TickCount64 - cast.ResolvedTicks) / 1000f;
                    if (sinceDeath > 0.5f)
                        continue; // will be cleaned up by toRemove (still in set)

                    toRemove.Remove(entityId);
                    _result.Add(cast);
                    continue;
                }

                // Entity alive but no longer casting (mob died mid-cast, or cast ended
                // without ActionResolve/CastCancel). Clean up immediately.
                if (!bchara.IsCasting)
                    continue; // stays in toRemove → gets removed

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

                // Phase 1: Detect native game omen (VfxContainer read for HasOmen + hook data)
                bool  hasOmen          = false;
                float omenRadiusFromHook = 0f; // captured from HookTrackedOmen.OmenRadius (a6)
                uint  hookOmenId         = 0;   // captured from HookTrackedOmen.OmenId   (a1)
                float hookRotation       = 0f;  // captured from HookTrackedOmen.OmenRotation (a5)
                unsafe
                {
                    nint addr       = obj.Address;
                    nint vfxDataPtr = *(nint*)((byte*)addr + VfxContainerOmenOffset);
                    if (vfxDataPtr != nint.Zero)
                    {
                        nint instancePtr = *(nint*)((byte*)vfxDataPtr + 0x1B8);
                        if (instancePtr != nint.Zero)
                        {
                            hasOmen      = true;
                            indicatorType = "NATIVE";
                            // Remove ALL hook entries for this entity — Step 3 won't double-process them.
                            // Capture OmenRadius (a6), OmenId (a1), OmenRotation (a5) from the first match.
                            float capturedRadius   = 0f;
                            uint  capturedOmenId   = 0;
                            float capturedRotation = 0f;
                            var toRemoveFromHook = new List<nint>();
                            foreach (var kv in _hookOmens)
                            {
                                if (kv.Value.EntityAddress == obj.Address)
                                {
                                    toRemoveFromHook.Add(kv.Key);
                                    if (capturedRadius == 0f)
                                    {
                                        capturedRadius   = kv.Value.OmenRadius;
                                        capturedOmenId   = kv.Value.OmenId;
                                        capturedRotation = kv.Value.OmenRotation;
                                    }
                                }
                            }
                            foreach (var hookKey in toRemoveFromHook)
                                _hookOmens.Remove(hookKey);
                            omenRadiusFromHook = capturedRadius;
                            hookOmenId         = capturedOmenId;
                            hookRotation       = capturedRotation;
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
                            isGroundTargeted);

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
                                entityId, actionId, shapeDef.Value, origin);
                            hasOmen       = true;
                            indicatorType = "CUSTOM:LUM";
                            shapeInfo     = FormatShapeDef(shapeDef.Value);
                        }
                    }
                }

                _active[entityId] = cast with
                {
                    Progress           = progress,
                    HasOmen            = hasOmen,
                    CasterPosition     = obj.Position,
                    IndicatorType      = indicatorType,
                    ShapeInfo          = shapeInfo,
                    CasterHitboxRadius = obj.HitboxRadius,
                    // Hook-captured a6: authoritative outer radius for native omens.
                    // Null for custom/Phase-2 omens — WorldOverlay uses Lumina + hitbox there.
                    OmenRadius         = indicatorType == "NATIVE" && omenRadiusFromHook > 0f
                                            ? omenRadiusFromHook : (float?)null,
                    // Hook-captured a1 + a5: authoritative omen ID and direction.
                    // Used by WorldOverlay Tier 1 shape resolution when Lumina OmenId is 0.
                    HookOmenId         = hookOmenId,
                    HookHeading        = hookOmenId > 0 ? hookRotation : (float?)null,
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

        // ── Step 3: Expire hook-tracked omens not handled by Step 2 ─────────
        // Color was written at spawn in CreateOmenDetour — nothing to update per-frame.
        // Just check liveness: Instance==null means the game destroyed the omen.
        if (_hookOmens.Count > 0)
        {
            var hookToRemove = new List<nint>();

            foreach (var kvp in _hookOmens)
            {
                nint vfxDataPtr = kvp.Key;
                nint instancePtr;
                unsafe { instancePtr = *(nint*)((byte*)vfxDataPtr + 0x1B8); }

                if (instancePtr == nint.Zero)
                {
                    DebugLog.Add("HOOK-REMOVE", $"VfxData=0x{vfxDataPtr:X} — Instance null (omen destroyed)");
                    hookToRemove.Add(vfxDataPtr);
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

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the per-frame entity maps. Called lazily — at most once per Scan().
    /// Populates both _entityMap (by GameObjectId) and _addressMap (by Address).
    /// </summary>
    private void BuildEntityMaps()
    {
        _entityMap.Clear();
        foreach (var obj in _objectTable)
        {
            if (obj.GameObjectId != 0)
                _entityMap[obj.GameObjectId] = obj;
        }
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
