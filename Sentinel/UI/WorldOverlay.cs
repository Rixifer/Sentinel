using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Sentinel.Core;
using System;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Sentinel.UI;

/// <summary>
/// Draws world-projected ImGui overlays: hitbox ring, action name labels, AoE hit warnings.
/// Called during the ImGui draw phase (Plugin.DrawUI), not Framework.Update.
/// All drawing is to the background draw list so it appears behind ImGui windows.
/// </summary>
public class WorldOverlay
{
    private readonly Plugin _plugin;
    private Configuration Config => _plugin.Config;

    public WorldOverlay(Plugin plugin)
    {
        _plugin = plugin;
    }

    public void Draw()
    {
        if (!Config.Enabled) return;

        var drawList  = ImGui.GetBackgroundDrawList();
        var playerObj = Plugin.ObjectTable.LocalPlayer;

        if (Config.ShowHitbox && playerObj != null)
        {
            bool shouldDraw = !Config.HitboxOnlyInCombat || IsInCombat();
            if (shouldDraw)
                DrawHitboxDot(drawList, playerObj);
        }

        if (Config.ShowActionNames)
            DrawActionNames(drawList);

        if (Config.ShowHitWarning && playerObj != null)
            DrawHitWarnings(drawList, playerObj);
    }

    // ── Feature 1: Player hitbox dot + range rings ────────────────────────

    private void DrawHitboxDot(ImDrawListPtr drawList,
        Dalamud.Game.ClientState.Objects.Types.IGameObject player)
    {
        var center = player.Position;

        if (!Plugin.GameGui.WorldToScreen(center, out var screenPos)) return;

        uint dotColor = ImGui.ColorConvertFloat4ToU32(Config.HitboxColor);

        // Filled dot — the player's AoE hitbox is this exact point
        drawList.AddCircleFilled(screenPos, Config.HitboxDotSize, dotColor, 16);

        // Dark outline for visibility against bright backgrounds
        if (Config.HitboxOutline)
        {
            uint outlineColor = ImGui.ColorConvertFloat4ToU32(Config.HitboxOutlineColor);
            drawList.AddCircle(screenPos, Config.HitboxDotSize + 1f, outlineColor, 16, 1f);
        }

        // Range Ring 1
        if (Config.ShowHitboxRing && Config.HitboxRingRadius > 0f)
            DrawWorldRing(drawList, center, Config.HitboxRingRadius,
                Config.HitboxRingColor, Config.HitboxRingThickness);

        // Range Ring 2
        if (Config.ShowHitboxRing2 && Config.HitboxRingRadius2 > 0f)
            DrawWorldRing(drawList, center, Config.HitboxRingRadius2,
                Config.HitboxRingColor2, Config.HitboxRingThickness2);
    }

    /// <summary>
    /// Projects a world-space circle onto the screen and draws it as one or more polyline
    /// segments, handling the case where parts of the ring pass behind the camera.
    /// When a vertex goes behind the camera, the current segment is stroked and a new one
    /// begins at the next visible vertex — preventing gaps or vanishing rings.
    /// </summary>
    private void DrawWorldRing(ImDrawListPtr drawList, Vector3 center,
        float radius, Vector4 color, float thickness)
    {
        const int segments   = 64;
        uint      col        = ImGui.ColorConvertFloat4ToU32(color);
        bool      prevVisible = false;

        for (int i = 0; i <= segments; i++)
        {
            float angle  = (float)(2.0 * Math.PI * i / segments);
            var worldPos = new Vector3(
                center.X + radius * MathF.Sin(angle),
                center.Y,
                center.Z + radius * MathF.Cos(angle));

            bool visible = Plugin.GameGui.WorldToScreen(worldPos, out var screenPos);

            if (visible)
            {
                drawList.PathLineTo(screenPos);
                prevVisible = true;
            }
            else if (prevVisible)
            {
                // Transition visible → off-screen: stroke accumulated segment and reset
                drawList.PathStroke(col, ImDrawFlags.None, thickness);
                prevVisible = false;
            }
            // If !visible && !prevVisible: still off-screen, nothing to do
        }

        // Stroke any remaining segment
        if (prevVisible)
            drawList.PathStroke(col, ImDrawFlags.None, thickness);
    }

    private static bool IsInCombat()
        => Plugin.Condition[ConditionFlag.InCombat];

    // ── Feature 2: Action name floating labels ────────────────────────────

    private void DrawActionNames(ImDrawListPtr drawList)
    {
        var casts = _plugin._lastCasts;
        if (casts == null) return;

        foreach (var cast in casts)
        {
            if (!cast.HasOmen) continue;
            if (string.IsNullOrEmpty(cast.ActionName)) continue;

            var worldPos = cast.IsGroundTargeted && cast.TargetPosition != Vector3.Zero
                ? cast.TargetPosition
                : cast.CasterPosition;
            worldPos.Y += 1.5f; // float above ground plane

            if (!Plugin.GameGui.WorldToScreen(worldPos, out var screenPos)) continue;

            string text      = cast.ActionName;
            var    textSize  = ImGui.CalcTextSize(text);
            var    textPos   = new Vector2(screenPos.X - textSize.X * 0.5f,
                                           screenPos.Y - textSize.Y * 0.5f);

            uint shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.8f));
            uint textColor   = ImGui.ColorConvertFloat4ToU32(Config.ActionNameColor);

            drawList.AddText(textPos + new Vector2(1f, 1f), shadowColor, text);
            drawList.AddText(textPos, textColor, text);
        }
    }

    // ── Feature 3: Hit detection warning ─────────────────────────────────

    private void DrawHitWarnings(ImDrawListPtr drawList,
        Dalamud.Game.ClientState.Objects.Types.IGameObject player)
    {
        var casts = _plugin._lastCasts;
        if (casts == null) return;

        var playerPos2D = new Vector2(player.Position.X, player.Position.Z);

        foreach (var cast in casts)
        {
            if (!cast.HasOmen) continue;

            var aoeCenter = cast.IsGroundTargeted && cast.TargetPosition != Vector3.Zero
                ? cast.TargetPosition
                : cast.CasterPosition;

            if (!IsPlayerInsideAoE(playerPos2D, aoeCenter, cast)) continue;

            var warningWorldPos = aoeCenter;
            warningWorldPos.Y += 3f; // above the action name label

            if (!Plugin.GameGui.WorldToScreen(warningWorldPos, out var screenPos)) continue;

            string warning     = "!";
            var    textSize    = ImGui.CalcTextSize(warning);
            var    basePos     = new Vector2(screenPos.X - textSize.X * 0.5f,
                                             screenPos.Y - textSize.Y);

            uint shadowColor  = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f));
            uint warningColor = ImGui.ColorConvertFloat4ToU32(Config.HitWarningColor);

            // Shadow
            drawList.AddText(basePos + new Vector2(2f, 2f), shadowColor, warning);
            // Bold effect — draw offset copies
            drawList.AddText(basePos,                         warningColor, warning);
            drawList.AddText(basePos + new Vector2(1f, 0f),  warningColor, warning);
            drawList.AddText(basePos + new Vector2(0f, 1f),  warningColor, warning);
        }
    }

    /// <summary>
    /// Approximate hit test using a circle approximation for all shape types.
    /// A circle is a conservative bound — it may trigger for shapes like cones or rects
    /// where the player is outside the actual hitbox but within the bounding circle.
    /// Accurate per-shape tests can be added later.
    /// </summary>
    private static bool IsPlayerInsideAoE(Vector2 playerPos2D, Vector3 aoeCenter, ActiveCast cast)
    {
        var   center2D = new Vector2(aoeCenter.X, aoeCenter.Z);
        float dist     = Vector2.Distance(playerPos2D, center2D);
        float radius   = EstimateAoERadius(cast);
        if (radius <= 0f) return false;
        return dist <= radius;
    }

    /// <summary>
    /// Extracts an approximate radius from the cast's ShapeInfo string.
    /// ShapeInfo formats: "Circle(8.0)", "Cone(12.0, 90°)", "Rect(40.0, 4.0)", "Donut(5.0/10.0)"
    /// Returns 0 if ShapeInfo is empty (native omens without custom shape data are skipped).
    /// </summary>
    private static float EstimateAoERadius(ActiveCast cast)
    {
        if (string.IsNullOrEmpty(cast.ShapeInfo)) return 0f;

        // Extract the first numeric value — always the primary radius/range
        var match = Regex.Match(cast.ShapeInfo, @"[\d.]+");
        if (match.Success && float.TryParse(match.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float r))
            return r;

        return 0f;
    }
}
