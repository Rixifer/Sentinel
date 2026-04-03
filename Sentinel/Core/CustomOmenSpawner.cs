using Sentinel.Data;
using System;
using System.Numerics;

namespace Sentinel.Core;

/// <summary>
/// Converts BMR shape data into custom-spawned omen VFX for untelegraphed mechanics.
/// Called from CastScanner when an enemy cast has no native game omen (omenId == 0).
/// </summary>
public class CustomOmenSpawner
{
    private readonly OmenVfxTracker _tracker;
    private readonly Configuration  _config;

    public CustomOmenSpawner(OmenVfxTracker tracker, OmenManager omenManager, Configuration config)
    {
        _tracker = tracker;
        _config  = config;
    }

    private Vector4 GetStaticOmenColor()
    {
        var color = _config.OmenColor;
        color = new Vector4(color.X, color.Y, color.Z, color.W * _config.OmenOpacity);
        float glow = _config.GlowIntensity;
        if (glow != 1.0f)
            color = new Vector4(color.X * glow, color.Y * glow, color.Z * glow, color.W);
        return color;
    }

    /// <summary>
    /// Spawns or updates a custom omen VFX for an untelegraphed cast.
    /// </summary>
    /// <param name="entityId">Caster entity ID — used as part of the tracker key.</param>
    /// <param name="actionId">Action being cast.</param>
    /// <param name="shape">BMR shape entry for this action.</param>
    /// <param name="position">World-space AoE origin (caster position or ground target).</param>
    /// <param name="heading">Cast direction in radians.</param>
    /// <param name="hitboxRadius">Caster hitbox radius — added to caster-centered circles.</param>
    /// <param name="isGroundTargeted">True if the AoE is placed at a target location.</param>
    public void SpawnOrUpdate(
        ulong entityId, uint actionId, BmrShapeEntry shape,
        Vector3 position, float heading, float hitboxRadius,
        bool isGroundTargeted)
    {
        var shapeDef = ConvertToShapeDefinition(shape, position, heading, hitboxRadius, isGroundTargeted);
        if (shapeDef == null) return;

        var resolved = OmenPathDecoder.ResolveOmenVfx(shapeDef.Value);
        if (resolved == null) return;

        var (avfxPath, size, rotation) = resolved.Value;
        var color = GetStaticOmenColor();

        string key = $"{entityId}##{actionId}";

        if (_tracker.IsTouched(key)) return;

        if (_tracker.TryTouchExisting(key, out var existing))
        {
            existing.UpdateTransform(position, size, rotation);
            existing.UpdateColor(color);
        }
        else
        {
            var vfx = OmenVfx.Create(avfxPath, position, size, rotation, color);
            if (vfx != null)
            {
                _tracker.TouchNew(key, vfx);
                Plugin.Log.Debug(
                    "[Sentinel][CUSTOM] Spawned omen for action {Id} ({Name}): {Path}",
                    actionId, shape.ActionName, avfxPath);
            }
        }
    }

    /// <summary>
    /// Spawns or updates a custom omen VFX from a pre-resolved ShapeDefinition.
    /// Used by the Lumina CastType fallback path when no BMR data is available.
    /// </summary>
    /// <param name="entityId">Caster entity ID — used as part of the tracker key.</param>
    /// <param name="actionId">Action being cast.</param>
    /// <param name="shape">Pre-built ShapeDefinition from AoEResolver.</param>
    /// <param name="position">World-space AoE origin.</param>
    public void SpawnOrUpdateFromShape(
        ulong entityId, uint actionId, ShapeDefinition shape,
        Vector3 position)
    {
        var resolved = OmenPathDecoder.ResolveOmenVfx(shape);
        if (resolved == null) return;

        var (avfxPath, size, rotation) = resolved.Value;
        var color = GetStaticOmenColor();

        string key = $"{entityId}##{actionId}";

        if (_tracker.IsTouched(key)) return;

        if (_tracker.TryTouchExisting(key, out var existing))
        {
            existing.UpdateTransform(position, size, rotation);
            existing.UpdateColor(color);
        }
        else
        {
            var vfx = OmenVfx.Create(avfxPath, position, size, rotation, color);
            if (vfx != null)
            {
                _tracker.TouchNew(key, vfx);
                Plugin.Log.Debug(
                    "[Sentinel][LUMINA-CUSTOM] Spawned fallback omen for action {Id}: {Path}",
                    actionId, avfxPath);
            }
        }
    }

    private static ShapeDefinition? ConvertToShapeDefinition(
        BmrShapeEntry bmr, Vector3 position, float heading,
        float hitboxRadius, bool isGroundTargeted)
    {
        ShapeType type = bmr.ShapeType switch
        {
            "Circle"      => ShapeType.Circle,
            "Cone"        => ShapeType.Cone,
            "Rect"        => ShapeType.Rect,
            "Donut"       => ShapeType.Donut,
            "DonutSector" => ShapeType.Donut,  // treat as donut for VFX
            "Cross"       => ShapeType.Cross,
            _             => (ShapeType)(-1),
        };
        if ((int)type == -1) return null;

        float radius      = bmr.Radius;
        float range       = bmr.LengthFront > 0 ? bmr.LengthFront : bmr.Radius;
        float halfWidth   = bmr.HalfWidth;
        float halfAngle   = bmr.HalfAngleDeg * MathF.PI / 180f;
        float innerRadius = bmr.InnerRadius;
        float outerRadius = bmr.OuterRadius > 0 ? bmr.OuterRadius : bmr.Radius;

        // For caster-centered circles, BMR stores raw EffectRange.
        // The game adds hitbox radius for these — match it so our VFX covers the true damage area.
        if (type == ShapeType.Circle && !isGroundTargeted && hitboxRadius > 0)
            radius += hitboxRadius;

        return new ShapeDefinition(
            Type:            type,
            Origin:          position,
            Heading:         heading,
            Radius:          type == ShapeType.Donut ? outerRadius : radius,
            InnerRadius:     innerRadius,
            Range:           range,
            HalfWidth:       halfWidth,
            HalfAngle:       halfAngle,
            Unavoidable:     false,
            IsGroundTargeted:isGroundTargeted,
            CastType:        0,
            OmenId:          0
        );
    }
}
