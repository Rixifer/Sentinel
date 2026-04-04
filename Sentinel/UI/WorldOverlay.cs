using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Sentinel.Core;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
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
    private readonly IDataManager _dataManager;
    private readonly IFontHandle? _axisFont;
    private readonly Dictionary<uint, float> _effectRangeCache = new();
    private readonly Dictionary<uint, float> _xAxisCache       = new();

    // Dedup set: prevents spamming HIT-DETECT logs for the same cast every frame.
    // Keys are "entityId##actionId". Entries are removed when the cast ends.
    private readonly HashSet<string> _loggedHits = new();

    // State-change tracking for ENTERED/EXITED diagnostics
    private bool _wasInsideAoE = false;

    // FFXIV UI warning icon (orange "!" — icon 60073).
    // If this ID doesn't yield a useful texture the code falls back to scaled ImGui text.
    private const uint WarningIconId = 60073u;

    // ── Shape types used internally for hit tests ─────────────────────────
    private enum HitShape { Circle, Cone, Rect, Donut }

    private struct ShapeParams
    {
        public HitShape Shape;
        public float Radius;      // Circle/Donut outer radius; used as display radius
        public float InnerRadius; // Donut only
        public float HalfAngle;   // Cone: half-angle in radians
        public float HalfWidth;   // Rect: half-width in yalms
        public float Range;       // Cone/Rect forward length in yalms
    }

    public WorldOverlay(Plugin plugin)
    {
        _plugin      = plugin;
        _dataManager = Plugin.DataManager;
        _axisFont    = plugin.AxisFont;
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

        if (Config.ShowCastBar)
            DrawCastBars(drawList);
        else if (Config.ShowActionNames)
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

        // Range Ring 3
        if (Config.ShowHitboxRing3 && Config.HitboxRingRadius3 > 0f)
            DrawWorldRing(drawList, center, Config.HitboxRingRadius3,
                Config.HitboxRingColor3, Config.HitboxRingThickness3);
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
        const int segments    = 64;
        uint      col         = ImGui.ColorConvertFloat4ToU32(color);
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

    // ── Feature 2a: Cast bar overlay ──────────────────────────────────────

    private void DrawCastBars(ImDrawListPtr drawList)
    {
        var casts = _plugin._lastCasts;
        if (casts == null) return;

        float barW = Config.CastBarWidth;
        float barH = Config.CastBarHeight;

        uint bgColor     = ImGui.ColorConvertFloat4ToU32(Config.CastBarBgColor);
        uint fillColor   = ImGui.ColorConvertFloat4ToU32(Config.CastBarFillColor);
        uint borderColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f));

        using (_axisFont?.Push())
        {
            var   font     = ImGui.GetFont();
            float nameSize = Config.ActionNameSize;
            float timeSize = MathF.Max(nameSize * 0.75f, 10f);

            foreach (var cast in casts)
            {
                if (!cast.HasOmen) continue;

                var worldPos = cast.IsGroundTargeted && cast.TargetPosition != Vector3.Zero
                    ? cast.TargetPosition
                    : cast.CasterPosition;
                worldPos.Y += 1.0f;

                if (!Plugin.GameGui.WorldToScreen(worldPos, out var screenPos)) continue;

                float progress = cast.Progress;

                // ── Action name above bar ─────────────────────────────────
                if (Config.ShowCastBarName && !string.IsNullOrEmpty(cast.ActionName))
                {
                    string text     = cast.ActionName;
                    var    textSize = ImGui.CalcTextSizeA(font, nameSize, float.MaxValue, 0f, text, out _);
                    var    textPos  = new Vector2(screenPos.X - textSize.X * 0.5f,
                                                  screenPos.Y - textSize.Y - barH - 2f);

                    uint shadowCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.8f));
                    uint textCol   = ImGui.ColorConvertFloat4ToU32(Config.ActionNameColor);
                    drawList.AddText(font, nameSize, textPos + new Vector2(1f, 1f), shadowCol, text, 0f);
                    drawList.AddText(font, nameSize, textPos, textCol, text, 0f);
                }

                // ── Bar background ────────────────────────────────────────
                var barTL = new Vector2(screenPos.X - barW * 0.5f, screenPos.Y - barH);
                var barBR = new Vector2(screenPos.X + barW * 0.5f, screenPos.Y);

                drawList.AddRectFilled(barTL, barBR, bgColor, 2f);

                // ── Fill ──────────────────────────────────────────────────
                float fillW  = barW * Math.Clamp(progress, 0f, 1f);
                var   fillBR = new Vector2(barTL.X + fillW, barBR.Y);
                if (fillW > 0f)
                    drawList.AddRectFilled(barTL, fillBR, fillColor, 2f);

                // ── Border ────────────────────────────────────────────────
                drawList.AddRect(barTL, barBR, borderColor, 2f, ImDrawFlags.None, 1f);

                // ── Remaining time below bar ──────────────────────────────
                if (Config.ShowCastBarTime && cast.TotalCastTime > 0f)
                {
                    float remaining = cast.TotalCastTime * (1f - progress);
                    string timeText = $"{remaining:F1}s";
                    var    tSize    = ImGui.CalcTextSizeA(font, timeSize, float.MaxValue, 0f, timeText, out _);
                    var    tPos     = new Vector2(screenPos.X - tSize.X * 0.5f, screenPos.Y + 2f);

                    uint shadowCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.8f));
                    uint timeCol   = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f));
                    drawList.AddText(font, timeSize, tPos + new Vector2(1f, 1f), shadowCol, timeText, 0f);
                    drawList.AddText(font, timeSize, tPos, timeCol, timeText, 0f);
                }
            }
        }
    }

    // ── Feature 2b: Action name floating labels (standalone, used when cast bar off) ──

    private void DrawActionNames(ImDrawListPtr drawList)
    {
        var casts = _plugin._lastCasts;
        if (casts == null) return;

        // Push the FFXIV Axis font for crisp rendering — scaling DOWN from 36px native stays sharp.
        // If AxisFont is null (load failed), _axisFont?.Push() returns null and using(null) is a no-op,
        // so ImGui.GetFont() falls back to the default font (blurry when scaled up, but functional).
        using (_axisFont?.Push())
        {
            var   font      = ImGui.GetFont(); // returns Axis font if pushed, default otherwise
            float fontSize  = Config.ActionNameSize;

            foreach (var cast in casts)
            {
                if (!cast.HasOmen) continue;
                if (string.IsNullOrEmpty(cast.ActionName)) continue;

                var worldPos = cast.IsGroundTargeted && cast.TargetPosition != Vector3.Zero
                    ? cast.TargetPosition
                    : cast.CasterPosition;
                worldPos.Y += 1.5f; // float above ground plane

                if (!Plugin.GameGui.WorldToScreen(worldPos, out var screenPos)) continue;

                string text     = cast.ActionName;
                var    textSize = ImGui.CalcTextSizeA(font, fontSize, float.MaxValue, 0f, text, out _);
                var    textPos  = new Vector2(screenPos.X - textSize.X * 0.5f,
                                              screenPos.Y - textSize.Y * 0.5f);

                uint shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.8f));
                uint textColor   = ImGui.ColorConvertFloat4ToU32(Config.ActionNameColor);

                drawList.AddText(font, fontSize, textPos + new Vector2(1f, 1f), shadowColor, text, 0f);
                drawList.AddText(font, fontSize, textPos, textColor, text, 0f);
            }
        }
    }

    // ── Feature 3: Hit detection warning ─────────────────────────────────

    private void DrawHitWarnings(ImDrawListPtr drawList,
        Dalamud.Game.ClientState.Objects.Types.IGameObject player)
    {
        var casts = _plugin._lastCasts;
        if (casts == null) return;

        var playerPos2D = new Vector2(player.Position.X, player.Position.Z);

        // Build the set of cast keys that hit the player this frame, and count omen-bearing casts
        var currentHitKeys = new HashSet<string>();
        bool anyHit        = false;
        int  castsWithOmen = 0;

        foreach (var cast in casts)
        {
            if (!cast.HasOmen) continue;
            castsWithOmen++;

            if (!IsPlayerHitByCast(playerPos2D, cast,
                    out var shapeName, out float radius, out float dist))
                continue;

            anyHit = true;
            string hitKey = $"{cast.EntityId}##{cast.ActionId}";
            currentHitKeys.Add(hitKey);

            // Log once per cast (not every frame)
            if (_loggedHits.Add(hitKey))
                DebugLog.Add("HIT-DETECT",
                    $"HIT: Action {cast.ActionId} \"{cast.ActionName}\" — " +
                    $"shape={shapeName}, radius={radius:F1}y, dist={dist:F1}y");
        }

        // Expire log entries whose casts are no longer active
        _loggedHits.IntersectWith(currentHitKeys);

        // ── State-change diagnostics (log only on transitions) ────────────
        if (anyHit && !_wasInsideAoE)
        {
            DebugLog.Add("HIT-DETECT",
                $"ENTERED — player=({playerPos2D.X:F1},{playerPos2D.Y:F1})");
            _wasInsideAoE = true;
        }
        else if (!anyHit && _wasInsideAoE)
        {
            // Dump why we stopped detecting — critical for diagnosing false disappearances
            var sb = new StringBuilder();
            sb.Append($"EXITED — player=({playerPos2D.X:F1},{playerPos2D.Y:F1}), ");
            sb.Append($"totalCasts={casts.Count}, withOmen={castsWithOmen}");

            foreach (var cast in casts)
            {
                if (!cast.HasOmen) continue;
                var origin   = cast.IsGroundTargeted && cast.TargetPosition != Vector3.Zero
                    ? cast.TargetPosition : cast.CasterPosition;
                var origin2D = new Vector2(origin.X, origin.Z);
                float dist   = Vector2.Distance(playerPos2D, origin2D);
                sb.Append($" | {cast.ActionId}@({origin2D.X:F1},{origin2D.Y:F1}) d={dist:F1}");
            }

            DebugLog.Add("HIT-DETECT", sb.ToString());
            _wasInsideAoE = false;
        }

        if (!anyHit) return;

        // ── Draw warning icon above the PLAYER'S head ─────────────────────
        // Always above the player (never over the AoE) so it's always visible.
        var warningWorldPos = player.Position;
        warningWorldPos.Y += 2.5f;

        if (!Plugin.GameGui.WorldToScreen(warningWorldPos, out var screenPos)) return;

        float iconSize = Config.HitWarningSize;
        uint  tintCol  = ImGui.ColorConvertFloat4ToU32(Config.HitWarningColor);

        // Attempt to render a game icon texture; fall back to scaled text if unavailable
        var lookup = new GameIconLookup(WarningIconId);
        var tex    = Plugin.TextureProvider.GetFromGameIcon(in lookup).GetWrapOrDefault();

        if (tex != null)
        {
            var topLeft     = new Vector2(screenPos.X - iconSize * 0.5f, screenPos.Y - iconSize);
            var bottomRight = new Vector2(screenPos.X + iconSize * 0.5f, screenPos.Y);
            drawList.AddImage(tex.Handle, topLeft, bottomRight, Vector2.Zero, Vector2.One, tintCol);
        }
        else
        {
            // Text fallback — single "!" at configured size, using Axis font for crisp rendering
            const string warning = "!";
            using (_axisFont?.Push())
            {
                var font     = ImGui.GetFont();
                var textSize = ImGui.CalcTextSizeA(font, iconSize, float.MaxValue, 0f, warning, out _);
                var basePos  = new Vector2(screenPos.X - textSize.X * 0.5f,
                                           screenPos.Y - textSize.Y);

                uint shadowCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f));
                drawList.AddText(font, iconSize, basePos + new Vector2(2f, 2f), shadowCol, warning, 0f);
                drawList.AddText(font, iconSize, basePos, tintCol, warning, 0f);
            }
        }
    }

    // ── Per-shape hit tests ───────────────────────────────────────────────

    /// <summary>
    /// Tests whether the player (2D XZ position) is inside the AoE described by <paramref name="cast"/>.
    /// Three-tier shape resolution:
    ///   Tier 1 — Omen VFX path (exact: cone angle from filename, donut ratio from table)
    ///   Tier 2 — ShapeInfo string (exact: from BMR/Lumina custom omen spawner)
    ///   Tier 3 — CastType from Lumina (fallback for untelegraphed actions with OmenId=0)
    /// </summary>
    private bool IsPlayerHitByCast(Vector2 playerPos, ActiveCast cast,
        out string shapeName, out float radius, out float dist)
    {
        shapeName = "Unknown";
        radius    = 0f;
        dist      = 0f;

        // Tier 1: omen path (exact shape type, cone angle, donut ratio).
        // Prefer hook-captured OmenId (a1) — it's authoritative even when Lumina returns 0.
        ShapeParams shape   = default;
        uint effectiveOmenId = cast.OmenId > 0 ? cast.OmenId : cast.HookOmenId;
        bool fromOmenPath    = effectiveOmenId > 0 &&
                               TryGetShapeFromOmenPath(effectiveOmenId, cast.ActionId, out shape);
        // Tier 2: ShapeInfo string (custom omens)  |  Tier 3: CastType fallback
        if (!fromOmenPath && !ParseShapeInfo(cast.ShapeInfo, out shape))
            GetShapeFromLumina(cast.CastType, cast.ActionId, out shape);

        // ── Radius application ─────────────────────────────────────────────
        // Strategy A: OmenRadius (a6) is authoritative — already includes caster hitbox.
        //             For donuts from the omen path, InnerRadius is a ratio [0..1] and
        //             must be converted to yalms now that we know the outer radius.
        // Strategy B: No a6 data — apply hitbox for caster-centred shapes manually.
        //             For donuts from the omen path, derive inner from EffectRange + hitbox.
        if (cast.OmenRadius.HasValue)
        {
            float r      = cast.OmenRadius.Value;
            shape.Radius = r;
            shape.Range  = r;
            // Donut from omen path: stored ratio → actual inner radius in yalms
            if (fromOmenPath && shape.Shape == HitShape.Donut &&
                shape.InnerRadius > 0f && shape.InnerRadius < 1f)
                shape.InnerRadius *= r;
        }
        else if (!cast.IsGroundTargeted && cast.CasterHitboxRadius > 0f)
        {
            float hb = cast.CasterHitboxRadius;
            if (fromOmenPath && shape.Shape == HitShape.Donut &&
                shape.InnerRadius > 0f && shape.InnerRadius < 1f)
            {
                // Ratio stored — compute inner from EffectRange + hitbox
                float outer       = GetEffectRange(cast.ActionId) + hb;
                shape.Radius      = outer;
                shape.Range       = outer;
                shape.InnerRadius = shape.InnerRadius * outer;
            }
            else
            {
                shape.Radius      += hb;
                shape.Range       += hb;
                shape.InnerRadius += hb;
            }
        }

        shapeName = shape.Shape.ToString();
        radius    = shape.Radius > 0f ? shape.Radius : shape.Range;

        if (radius <= 0f && shape.Range <= 0f) return false;

        // Origin: cones/rects emanate from the caster; circles/donuts use the ground target
        Vector3 origin3D = (shape.Shape == HitShape.Circle || shape.Shape == HitShape.Donut)
            && cast.IsGroundTargeted && cast.TargetPosition != Vector3.Zero
                ? cast.TargetPosition
                : cast.CasterPosition;

        var origin2D = new Vector2(origin3D.X, origin3D.Z);
        dist = Vector2.Distance(playerPos, origin2D);

        // Use hook-captured a5 when available — it's the authoritative omen direction.
        // The CastStart packet's Rotation can differ (e.g. stored as target heading, not omen heading).
        float heading = cast.HookHeading ?? cast.Heading;

        return IsPointInShape(playerPos, origin2D, heading, shape);
    }

    private static bool IsPointInShape(Vector2 point, Vector2 origin,
        float heading, ShapeParams shape)
    {
        switch (shape.Shape)
        {
            case HitShape.Circle:
                return Vector2.Distance(point, origin) <= shape.Radius;

            case HitShape.Donut:
            {
                float d = Vector2.Distance(point, origin);
                return d >= shape.InnerRadius && d <= shape.Radius;
            }

            case HitShape.Cone:
                return IsPointInCone(point, origin, heading, shape.Range, shape.HalfAngle);

            case HitShape.Rect:
                return IsPointInRect(point, origin, heading, shape.Range, shape.HalfWidth);

            default:
                return Vector2.Distance(point, origin) <= shape.Radius;
        }
    }

    /// <summary>
    /// Cone hit test. heading=0 → facing +Z (south); increases clockwise when viewed from above.
    /// The cone extends <paramref name="range"/> yalms forward within ±<paramref name="halfAngle"/> radians.
    /// </summary>
    private static bool IsPointInCone(Vector2 point, Vector2 origin,
        float heading, float range, float halfAngle)
    {
        var   toPoint = point - origin;
        float d       = toPoint.Length();
        if (d > range || d < 0.001f) return false;

        // Forward vector from heading: heading=0 → +Z → (0,1) in (X,Z) space
        var   forward = new Vector2(MathF.Sin(heading), MathF.Cos(heading));
        float dot     = Vector2.Dot(Vector2.Normalize(toPoint), forward);
        float angle   = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        return angle <= halfAngle;
    }

    /// <summary>
    /// Rect hit test. The rectangle extends <paramref name="range"/> yalms forward from the
    /// origin and ±<paramref name="halfWidth"/> yalms laterally.
    /// </summary>
    private static bool IsPointInRect(Vector2 point, Vector2 origin,
        float heading, float range, float halfWidth)
    {
        var toPoint = point - origin;

        // Project into caster-local axes: forward = (sin h, cos h); right = (cos h, -sin h)
        float fX = MathF.Sin(heading);
        float fZ = MathF.Cos(heading);
        float localForward = toPoint.X * fX  + toPoint.Y * fZ;
        float localRight   = toPoint.X * fZ  - toPoint.Y * fX;

        return localForward >= 0f && localForward <= range
            && MathF.Abs(localRight) <= halfWidth;
    }

    // ── Shape parsers ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses a ShapeInfo string produced by FormatBmrShape / FormatShapeDef.
    /// Formats: "Circle(r)", "Cone(range, totalAngle°)", "Rect(range × halfWidth)",
    ///          "Donut(inner/outer)", "Cross(range × halfWidth)", "DonutSec(inner/outer)"
    /// Returns false if the string is empty or unrecognised.
    /// </summary>
    private static bool ParseShapeInfo(string info, out ShapeParams shape)
    {
        shape = default;
        if (string.IsNullOrEmpty(info)) return false;

        float[] nums = ExtractNumbers(info);
        if (nums.Length == 0) return false;

        if (info.StartsWith("Circle", StringComparison.OrdinalIgnoreCase))
        {
            shape = new ShapeParams { Shape = HitShape.Circle, Radius = nums[0] };
            return true;
        }

        if (info.StartsWith("Cone", StringComparison.OrdinalIgnoreCase) && nums.Length >= 2)
        {
            // nums[1] is the TOTAL cone angle in degrees; halfAngle = total/2 in radians
            float halfAngle = nums[1] * 0.5f * MathF.PI / 180f;
            shape = new ShapeParams
            {
                Shape     = HitShape.Cone,
                Range     = nums[0],
                Radius    = nums[0],
                HalfAngle = halfAngle,
            };
            return true;
        }

        if (info.StartsWith("Rect", StringComparison.OrdinalIgnoreCase) && nums.Length >= 2)
        {
            // nums[1] is half-width (as stored in ShapeDefinition.HalfWidth)
            shape = new ShapeParams
            {
                Shape     = HitShape.Rect,
                Range     = nums[0],
                Radius    = nums[0],
                HalfWidth = nums[1],
            };
            return true;
        }

        if ((info.StartsWith("Donut", StringComparison.OrdinalIgnoreCase)) && nums.Length >= 2)
        {
            // "Donut(inner/outer)" or "DonutSec(inner/outer)"
            shape = new ShapeParams
            {
                Shape       = HitShape.Donut,
                InnerRadius = nums[0],
                Radius      = nums[1],
            };
            return true;
        }

        if (info.StartsWith("Cross", StringComparison.OrdinalIgnoreCase) && nums.Length >= 1)
        {
            // Cross = two overlapping rects; approximate as a circle for hit detection
            shape = new ShapeParams { Shape = HitShape.Circle, Radius = nums[0] };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Derives ShapeParams from a native omen's CastType and Lumina Action data.
    /// Used when ShapeInfo is empty (hook-detected / network-detected native omens).
    /// </summary>
    private void GetShapeFromLumina(byte castType, uint actionId, out ShapeParams shape)
    {
        float range = GetEffectRange(actionId);
        float xAxis = GetXAxisModifier(actionId);

        shape = castType switch
        {
            // CT3 / CT13 = Cone (verified: omen IDs 3, 4, 5, 105, 184, 185, 508 are all cones)
            3 or 13 => new ShapeParams
            {
                Shape     = HitShape.Cone,
                Range     = range,
                Radius    = range,
                HalfAngle = (xAxis > 0f ? xAxis * 0.5f : 45f) * MathF.PI / 180f,
            },
            // CT4 / CT12 = Rect (verified: omen ID 2 is rect; CT12 is a wider/ground variant)
            4 or 12 => new ShapeParams
            {
                Shape     = HitShape.Rect,
                Range     = range,
                Radius    = range,
                HalfWidth = xAxis > 0f ? xAxis * 0.5f : 2f,
            },
            // CT8 = Charge/dash line (ER=0, actual range is distance to target — unknown here)
            // Skip hit detection for charges since we can't determine the line length.
            8 => new ShapeParams { Shape = HitShape.Rect, Range = 0f, Radius = 0f },
            // CT10 = Donut (inner radius not available from Lumina — 0 means safe zone unknown)
            10 => new ShapeParams
            {
                Shape       = HitShape.Donut,
                Radius      = range,
                InnerRadius = 0f,
            },
            // CT11 = Cross (two overlapping rects at 90°) — no HitShape.Cross yet,
            // approximate as circle. Conservative: detects in the gaps between arms.
            11 => new ShapeParams { Shape = HitShape.Circle, Radius = range },
            // CT2, CT5, CT7 = Circle variants. Everything else falls to circle.
            _ => new ShapeParams { Shape = HitShape.Circle, Radius = range },
        };
    }

    // ── Omen path shape resolver (Tier 1) ────────────────────────────────

    /// <summary>
    /// Resolves the hit-detection shape directly from the omen VFX filename.
    /// <list type="bullet">
    ///   <item>Cone angle from <c>gl_fan{NNN}_</c> suffix</item>
    ///   <item>Donut ratio from the <see cref="OmenPathDecoder"/> table (InnerRadius is stored
    ///         as a ratio [0..1] — the caller must multiply by the actual outer radius)</item>
    ///   <item>Rect HalfWidth from Lumina XAxisModifier (not encoded in the path)</item>
    /// </list>
    /// Returns false when the omen ID has no path or an unrecognised filename pattern.
    /// </summary>
    private bool TryGetShapeFromOmenPath(uint omenId, uint actionId, out ShapeParams shape)
    {
        shape = default;

        string? path = _plugin._omenReader.GetOmenPath(omenId);
        if (path == null) return false;

        ShapeType? shapeType = OmenPathDecoder.InferShapeType(path);
        if (shapeType == null)
        {
            Plugin.Log.Debug(
                "[Sentinel][OMEN-SHAPE] Unrecognized omen path for OmenId={Id}: \"{Path}\"",
                omenId, path);
            return false;
        }

        switch (shapeType.Value)
        {
            case ShapeType.Circle:
                shape = new ShapeParams { Shape = HitShape.Circle };
                return true;

            case ShapeType.Cone:
            {
                // Parse total angle from filename (e.g. gl_fan090 → 90°); default 90° if missing
                int totalDeg = OmenPathDecoder.ParseConeAngle(path) ?? 90;
                shape = new ShapeParams
                {
                    Shape     = HitShape.Cone,
                    HalfAngle = totalDeg * 0.5f * MathF.PI / 180f,
                    // Range/Radius filled in from OmenRadius or EffectRange by the caller
                };
                return true;
            }

            case ShapeType.Rect:
            {
                // Width not encoded in the omen filename — use Lumina XAxisModifier
                float xAxis = GetXAxisModifier(actionId);
                shape = new ShapeParams
                {
                    Shape     = HitShape.Rect,
                    HalfWidth = xAxis > 0f ? xAxis * 0.5f : 2f,
                };
                return true;
            }

            case ShapeType.Donut:
            {
                // Store ratio [0..1] in InnerRadius — caller converts to yalms once outer radius known
                float ratio = OmenPathDecoder.GetDonutRatio(path) ?? 0f;
                shape = new ShapeParams
                {
                    Shape       = HitShape.Donut,
                    InnerRadius = ratio,
                };
                return true;
            }

            default:
                return false;
        }
    }

    // ── Lumina helpers ────────────────────────────────────────────────────

    private float GetEffectRange(uint actionId)
    {
        if (_effectRangeCache.TryGetValue(actionId, out var cached))
            return cached;

        float range = 0f;
        var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        var row   = sheet?.GetRowOrDefault(actionId);
        if (row.HasValue) range = row.Value.EffectRange;

        _effectRangeCache[actionId] = range;
        return range;
    }

    private float GetXAxisModifier(uint actionId)
    {
        if (_xAxisCache.TryGetValue(actionId, out var cached))
            return cached;

        float val = 0f;
        var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        var row   = sheet?.GetRowOrDefault(actionId);
        if (row.HasValue) val = row.Value.XAxisModifier;

        _xAxisCache[actionId] = val;
        return val;
    }

    // ── Utility ───────────────────────────────────────────────────────────

    /// <summary>Extracts all decimal numbers from a string in order.</summary>
    private static float[] ExtractNumbers(string text)
    {
        var matches = Regex.Matches(text, @"\d+(?:\.\d+)?");
        var result  = new float[matches.Count];
        for (int i = 0; i < matches.Count; i++)
            float.TryParse(matches[i].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out result[i]);
        return result;
    }
}
