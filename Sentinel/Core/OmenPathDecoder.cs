using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Sentinel.Core;

/// <summary>
/// Resolves a <see cref="ShapeDefinition"/> to an .avfx path and scale vector,
/// and provides inverse helpers for decoding shape metadata from an omen path.
/// All VFX name tables are taken verbatim from Pictomancy's VfxRendererExtensions.cs.
/// </summary>
public static class OmenPathDecoder
{
    private const float DefaultHeight = 7f;

    // ── Cone table ────────────────────────────────────────────────────────────
    // 16 entries. Source: Pictomancy VfxRendererExtensions.GetOmenConeForAngle
    private static string? GetOmenConeForAngle(int angleDegrees) => angleDegrees switch
    {
        15  => "gl_fan015_0x",
        20  => "gl_fan020_0f",
        30  => "gl_fan030_1bf",
        45  => "gl_fan045_1bf",
        60  => "gl_fan060_1bf",
        80  => "gl_fan80_o0g",
        90  => "gl_fan090_1bf",
        120 => "gl_fan120_1bf",
        130 => "gl_fan130_0x",
        135 => "gl_fan135_c0g",
        150 => "gl_fan150_1bf",
        180 => "gl_fan180_1bf",
        210 => "gl_fan210_1bf",
        225 => "gl_fan225_c0g",
        270 => "gl_fan270_0100af",
        360 => "general_1bf",
        _   => null,
    };

    // ── Donut table ───────────────────────────────────────────────────────────
    // 36 entries. Source: Pictomancy VfxRendererExtensions.OmenDonutHoleSizes
    private static readonly (string Name, float Ratio)[] DonutTable =
    [
        ("gl_sircle_5003bf",        0.07f),
        ("gl_sircle_7006x",         0.086f),
        ("gl_sircle_1034bf",        0.1138f),
        ("gl_sircle_4005bf",        0.126f),
        ("gl_circle_5007_x1",       0.14f),
        ("gl_sircle_6009at",        1.5f),
        ("gl_sircle_6010bf",        0.166f),
        ("gl_sircle_1703x",         0.1767f),
        ("gl_sircle_2004bv",        0.2f),
        ("gl_sircle_7015k1",        0.215f),
        ("gl_sircle_3007bx",        0.233f),
        ("gl_sircle_2005bf",        0.25f),
        ("gl_sircle_1905bf",        0.263f),
        ("gl_sircle_3008bf",        0.266f),
        ("gl_sircle_1805r1",        0.277f),
        ("gl_sircle_4012c",         0.3f),
        ("x6r8_b4_donut13m_4_01k1", 0.311f),
        ("gl_sircle_2508_o0t1",     0.32f),
        ("x6fa_donut01_o0a1",       0.333f),
        ("gl_sircle_1505bt1",       0.335f),
        ("gl_sircle_1907y0x",       0.37f),
        ("gl_sircle_2008bi",        0.4f),
        ("gl_sircle_3014bf",        0.466f),
        ("gl_sircle_2010bf",        0.5f),
        ("gl_sircle_4021x",         0.525f),
        ("gl_sircle_2011v",         0.55f),
        ("gl_sircle_3520x",         0.572f),
        ("gl_sircle_5030c",         0.6f),
        ("gl_sircle_1610_o0v",      0.625f),
        ("gl_sircle_1510bx",        0.666f),
        ("gl_sircle_1710_o0p",      0.71f),
        ("gl_sircle_2316_o0p",      0.733f),
        ("gl_sircle_2015bx",        0.75f),
        ("gl_sircle_1109w",         0.82f),
        ("gl_sircle_1715w",         0.886f),
        ("gl_sircle_2018w",         0.9f),
    ];

    /// <summary>
    /// Finds the donut omen whose ratio is closest to <paramref name="ratio"/>,
    /// within a tolerance of 0.02. Returns null if no entry is within tolerance.
    /// </summary>
    private static string? GetDonutOmenForRatio(float ratio)
    {
        const float Tolerance = 0.02f;
        string? bestName  = null;
        float   bestDelta = float.MaxValue;

        foreach (var (name, tableRatio) in DonutTable)
        {
            float delta = MathF.Abs(ratio - tableRatio);
            if (delta < Tolerance && delta < bestDelta)
            {
                bestDelta = delta;
                bestName  = name;
            }
        }

        return bestName;
    }

    // ── Primary resolver ─────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <see cref="ShapeDefinition"/> to an (.avfx path, size, rotation) triple.
    /// Returns null for Cross shapes or when no matching cone/donut VFX exists.
    /// </summary>
    public static (string AvfxPath, Vector3 Size, float Rotation)? ResolveOmenVfx(ShapeDefinition shape)
    {
        switch (shape.Type)
        {
            case ShapeType.Circle:
                return (
                    "vfx/omen/eff/general_1bf.avfx",
                    new Vector3(shape.Radius, DefaultHeight, shape.Radius),
                    0f
                );

            case ShapeType.Cone:
            {
                int totalAngleDeg = (int)Math.Round(shape.HalfAngle * 2 * 180.0 / MathF.PI);
                string? coneName  = GetOmenConeForAngle(totalAngleDeg);
                if (coneName == null) return null;

                return (
                    $"vfx/omen/eff/{coneName}.avfx",
                    new Vector3(shape.Range, DefaultHeight, shape.Range),
                    shape.Heading
                );
            }

            case ShapeType.Rect:
                return (
                    "vfx/omen/eff/general02f.avfx",
                    new Vector3(shape.HalfWidth, DefaultHeight, shape.Range),
                    shape.Heading
                );

            case ShapeType.Donut:
            {
                float ratio      = shape.InnerRadius / shape.Radius;
                string? donutName = GetDonutOmenForRatio(ratio);
                if (donutName == null) return null;

                return (
                    $"vfx/omen/eff/{donutName}.avfx",
                    new Vector3(shape.Radius, DefaultHeight, shape.Radius),
                    0f
                );
            }

            case ShapeType.Cross:
                return null;

            default:
                return null;
        }
    }

    // ── Inverse helpers ───────────────────────────────────────────────────────

    private static readonly string OmenPrefix = "vfx/omen/eff/";

    private static string StripPrefix(string omenPath)
    {
        if (omenPath.StartsWith(OmenPrefix, StringComparison.OrdinalIgnoreCase))
            return omenPath[OmenPrefix.Length..].Replace(".avfx", "", StringComparison.OrdinalIgnoreCase);
        return omenPath;
    }

    /// <summary>
    /// Infers the <see cref="ShapeType"/> from an omen VFX path by pattern-matching the filename.
    /// Returns null if the shape cannot be determined.
    /// </summary>
    public static ShapeType? InferShapeType(string omenPath)
    {
        string name = StripPrefix(omenPath);

        if (name.Contains("fan",     StringComparison.OrdinalIgnoreCase)) return ShapeType.Cone;
        if (name.Contains("sircle",  StringComparison.OrdinalIgnoreCase)) return ShapeType.Donut;
        if (name.StartsWith("general_1",  StringComparison.OrdinalIgnoreCase)) return ShapeType.Circle;
        if (name.StartsWith("general02",  StringComparison.OrdinalIgnoreCase)) return ShapeType.Rect;
        if (name.StartsWith("general_x02", StringComparison.OrdinalIgnoreCase)) return ShapeType.Rect;

        return null;
    }

    /// <summary>
    /// Parses the cone half-angle (in degrees) from a fan omen path such as
    /// <c>vfx/omen/eff/gl_fan090_1bf.avfx</c>. Returns null if not a cone omen.
    /// </summary>
    public static int? ParseConeAngle(string omenPath)
    {
        string name = StripPrefix(omenPath);
        var match = Regex.Match(name, @"gl_fan(\d+)_", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (int.TryParse(match.Groups[1].Value, out int deg))
            return deg;
        return null;
    }

    /// <summary>
    /// Returns the inner/outer radius ratio for a donut omen path, by looking up
    /// the filename in <see cref="DonutTable"/>. Returns null if not found.
    /// </summary>
    public static float? GetDonutRatio(string omenPath)
    {
        string name = StripPrefix(omenPath);
        foreach (var (tableName, ratio) in DonutTable)
        {
            if (string.Equals(tableName, name, StringComparison.OrdinalIgnoreCase))
                return ratio;
        }
        return null;
    }
}
