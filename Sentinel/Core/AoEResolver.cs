using Dalamud.Plugin.Services;
using Sentinel.Data;
using System;
using System.Collections.Generic;
using System.Numerics;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Sentinel.Core;

public class AoEResolver
{
    private const float DefaultConeHalfAngle  = MathF.PI / 4f; // 45 deg half = 90 deg total
    private const float DefaultDonutInnerRatio = 0.5f;

    private readonly IDataManager _dataManager;
    private readonly Dictionary<uint, ShapeDefinition?> _cache = new();

    private int _cacheHits;
    private int _cacheMisses;
    public int CacheHits   => _cacheHits;
    public int CacheMisses => _cacheMisses;
    public int CacheSize   => _cache.Count;

    public AoEResolver(IDataManager dataManager)
    {
        _dataManager = dataManager;
    }

    /// <summary>
    /// Resolves an action into a world-positioned ShapeDefinition.
    /// IsGroundTargeted is determined from action.TargetArea (Lumina), not CastType.
    /// Hitbox radius is only added for caster-centred shapes (IsGroundTargeted == false).
    /// </summary>
    public ShapeDefinition? Resolve(uint actionId, Vector3 origin, float heading, float hitboxRadius)
    {
        if (!_cache.TryGetValue(actionId, out var cached))
        {
            cached = BuildTemplate(actionId);
            _cache[actionId] = cached;
            _cacheMisses++;

            if (cached.HasValue)
                Plugin.Log.Debug("[Sentinel] Cached action {Id}: shape={Shape}, ground={G}",
                    actionId, cached.Value.Type, cached.Value.IsGroundTargeted);
            else
                Plugin.Log.Verbose("[Sentinel] Action {Id}: no AoE shape (skip)", actionId);
        }
        else
        {
            _cacheHits++;
        }

        if (cached == null) return null;

        // Stamp world position and heading onto the template
        var result = cached.Value with { Origin = origin, Heading = heading };

        // Hitbox radius is ONLY added to caster-centred circles.
        // Evidence from CreateOmen logs:
        //   Quaver (circle, CT5):     native omen a6=9.5  = EffectRange(6) + hitbox(3.5) ✓
        //   Fluid Swing (cone, CT13): native omen a6=11.0 = EffectRange(11) only          ✓
        //   Mud Stream (ground, CT2): native omen a6=6.0  = EffectRange(6) only            ✓
        // Cones, rects, donuts, crosses, and ground-targeted shapes do NOT include hitbox.
        if (result.Type == ShapeType.Circle && !result.IsGroundTargeted && hitboxRadius > 0f)
        {
            result = result with { Radius = result.Radius + hitboxRadius };
        }

        return result;
    }

    private ShapeDefinition? BuildTemplate(uint actionId)
    {
        var sheet = _dataManager.GetExcelSheet<LuminaAction>();
        if (sheet == null) return null;

        var row = sheet.GetRowOrDefault(actionId);
        if (!row.HasValue) return null;
        var action = row.Value;

        byte  castType = action.CastType;
        float range    = action.EffectRange;
        float xAxis    = action.XAxisModifier;

        // KEY FIX: Use Lumina's TargetArea field for ground-targeting, NOT CastType.
        // CastType 7/8 are ground-targeted by convention, but some unusual actions
        // break that convention. action.TargetArea is the authoritative source.
        bool isGroundTargeted = action.TargetArea;

        // Read Omen sheet RowId for cross-referencing with CreateOmen hook
        uint omenId = 0;
        try { omenId = action.Omen.RowId; } catch { }

        // Apply overrides
        if (Overrides.Table.TryGetValue(actionId, out var ov))
        {
            if (ov.Range.HasValue)     range = ov.Range.Value;
            if (ov.HalfWidth.HasValue) xAxis = ov.HalfWidth.Value * 2f;
        }

        switch (castType)
        {
            case 2: case 5: case 7: // circle (caster-centred or ground-targeted)
                // CastType 5 is a caster-centered AoE (often proximity damage) — it IS dodgeable.
                // Only CastType 6 is truly unavoidable (raidwide, no positional dodge).
                return new ShapeDefinition(ShapeType.Circle, default, 0f,
                    Radius: range, InnerRadius: 0f, Range: 0f, HalfWidth: 0f, HalfAngle: 0f,
                    Unavoidable: false, IsGroundTargeted: isGroundTargeted, CastType: castType,
                    OmenId: omenId);

            case 3: case 13: // cone from caster
            {
                float halfAngle = DefaultConeHalfAngle;
                if (Overrides.Table.TryGetValue(actionId, out var cov) && cov.ConeAngle.HasValue)
                    halfAngle = cov.ConeAngle.Value / 2f;
                return new ShapeDefinition(ShapeType.Cone, default, 0f,
                    Radius: 0f, InnerRadius: 0f, Range: range, HalfWidth: 0f, HalfAngle: halfAngle,
                    Unavoidable: false, IsGroundTargeted: isGroundTargeted, CastType: castType,
                    OmenId: omenId);
            }

            case 4: case 8: case 12: // rect (caster-centred or ground-targeted)
            {
                float hw = xAxis > 0 ? xAxis / 2f : 2f;
                if (Overrides.Table.TryGetValue(actionId, out var rov) && rov.HalfWidth.HasValue)
                    hw = rov.HalfWidth.Value;
                return new ShapeDefinition(ShapeType.Rect, default, 0f,
                    Radius: 0f, InnerRadius: 0f, Range: range, HalfWidth: hw, HalfAngle: 0f,
                    Unavoidable: false, IsGroundTargeted: isGroundTargeted, CastType: castType,
                    OmenId: omenId);
            }

            case 10: // donut (caster-centred)
            {
                float inner = range * DefaultDonutInnerRatio;
                if (Overrides.Table.TryGetValue(actionId, out var dov) && dov.InnerRadius.HasValue)
                    inner = dov.InnerRadius.Value;
                return new ShapeDefinition(ShapeType.Donut, default, 0f,
                    Radius: range, InnerRadius: inner, Range: 0f, HalfWidth: 0f, HalfAngle: 0f,
                    Unavoidable: false, IsGroundTargeted: isGroundTargeted, CastType: castType,
                    OmenId: omenId);
            }

            case 11: // cross (caster-centred)
            {
                float hw = xAxis > 0 ? xAxis / 2f : 2f;
                if (Overrides.Table.TryGetValue(actionId, out var crov) && crov.HalfWidth.HasValue)
                    hw = crov.HalfWidth.Value;
                return new ShapeDefinition(ShapeType.Cross, default, 0f,
                    Radius: 0f, InnerRadius: 0f, Range: range, HalfWidth: hw, HalfAngle: 0f,
                    Unavoidable: false, IsGroundTargeted: isGroundTargeted, CastType: castType,
                    OmenId: omenId);
            }

            case 1:  // single target — skip
            case 6:  // unavoidable raidwide with no position — skip
            default:
                return null;
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
        _cacheHits   = 0;
        _cacheMisses = 0;
    }
}
