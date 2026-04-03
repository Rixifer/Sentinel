using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace Sentinel.UI;

public class ConfigWindow : Window
{
    private readonly Plugin _plugin;
    private Configuration Config => _plugin.Config;

    // Preset detection helpers
    private static readonly string[] PresetNames =
    {
        "Custom",
        "Default (Yellow > Red)",
        "Deuteranopia",
        "Protanopia",
        "Tritanopia",
    };

    private static readonly (Vector4 Start, Vector4 End)[] PresetColors =
    {
        (default, default),                                                             // 0 Custom (unused)
        (new(0.996f, 0.961f, 0.471f, 1f), new(0.996f, 0.235f, 0.235f, 1f)),          // 1 Default
        (new(0.2f,  0.6f,   1.0f,   1f), new(0.7f,   0.2f,   1.0f,   1f)),           // 2 Deuteranopia
        (new(0.0f,  0.9f,   0.9f,   1f), new(0.9f,   0.0f,   0.9f,   1f)),           // 3 Protanopia
        (new(1.0f,  0.5f,   0.7f,   1f), new(0.9f,   0.1f,   0.1f,   1f)),           // 4 Tritanopia
    };

    public ConfigWindow(Plugin plugin) : base("Sentinel###SentinelConfig")
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 360),
            MaximumSize = new Vector2(700, 700),
        };
    }

    public override void Draw()
    {
        bool changed = false;

        // ── Header ──────────────────────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.65f, 0.0f, 1.0f));
        ImGui.SetWindowFontScale(1.4f);
        ImGui.Text("Sentinel");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextDisabled("  Enhanced Omen System");

        ImGui.Separator();

        // ── Enable / Disable ─────────────────────────────────────────────────
        bool enabled = Config.Enabled;
        ImGui.PushStyleColor(ImGuiCol.Button, enabled
            ? new Vector4(0.15f, 0.55f, 0.15f, 1.0f)
            : new Vector4(0.45f, 0.10f, 0.10f, 1.0f));

        if (ImGui.Button(enabled ? "  ENABLED  " : "  DISABLED  "))
        {
            Config.Enabled = !Config.Enabled;
            changed = true;
        }
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextDisabled("(or /sentinel on / off)");

        // ── Status ───────────────────────────────────────────────────────────
        ImGui.Spacing();
        int casts     = _plugin._scanner.LastScanCastCount;
        int recolored = _plugin._omenManager.LastRecolorCount;
        ImGui.TextDisabled(
            $"Active: {casts} cast{(casts != 1 ? "s" : "")}  |  " +
            $"Recoloring: {recolored} omen{(recolored != 1 ? "s" : "")}");
        ImGui.Separator();
        ImGui.Spacing();

        // ── Tabs ─────────────────────────────────────────────────────────────
        if (ImGui.BeginTabBar("SentinelTabs"))
        {
            DrawAoETab(ref changed);
            DrawHitboxTab(ref changed);
            DrawShapesTab(ref changed);
            DrawPerformanceTab(ref changed);
            ImGui.EndTabBar();
        }

        if (changed) Config.Save();
    }

    // ── AoE (indicator style + colors + danger warning) ───────────────────────
    private void DrawAoETab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("AoE")) return;

        // ── AoE Style (dropdown) ──────────────────────────────────────────
        ImGui.Text("AoE Style:");
        int style = (int)Config.IndicatorStyle;
        string[] styleNames = { "Default", "Force Enhanced (Dawntrail)", "Force Legacy" };
        ImGui.SetNextItemWidth(250f);
        if (ImGui.Combo("##omenStyle", ref style, styleNames, styleNames.Length))
        {
            Config.IndicatorStyle = (OmenStyle)style;
            changed = true;
        }
        string tooltip = Config.IndicatorStyle switch
        {
            OmenStyle.Default  => "Uses whichever indicator style the game assigns to each attack.",
            OmenStyle.ForceNew => "Upgrades all indicators to the Dawntrail-era style with vertical light wall borders for improved edge visibility.",
            OmenStyle.ForceOld => "Uses the original flat ground indicators without light wall borders.",
            _                  => "",
        };
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        ImGui.Spacing();
        ImGui.Separator();

        // ── Action Names ──────────────────────────────────────────────────
        ImGui.Text("Action Names:");
        changed |= ImGui.Checkbox("Show action names above AoEs", ref Config.ShowActionNames);
        ImGui.TextDisabled("  Displays the ability name floating above each active AoE indicator.");
        if (Config.ShowActionNames)
        {
            changed |= ImGui.ColorEdit4("##actionNameColor", ref Config.ActionNameColor,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
            ImGui.SameLine();
            ImGui.Text("Label color");
            ImGui.SetNextItemWidth(200f);
            changed |= ImGui.SliderFloat("##actionNameSize", ref Config.ActionNameSize, 10f, 36f, "%.0f px");
            ImGui.SameLine();
            ImGui.Text("Text size");
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── Hit Detection ─────────────────────────────────────────────────
        ImGui.Text("Hit Detection:");
        changed |= ImGui.Checkbox("Warn when standing in AoE", ref Config.ShowHitWarning);
        ImGui.TextDisabled("  Shows ! / !! / !!! based on the size of the AoE you are standing in.");
        if (Config.ShowHitWarning)
        {
            changed |= ImGui.ColorEdit4("##hitWarnColor", ref Config.HitWarningColor,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
            ImGui.SameLine();
            ImGui.Text("Warning color");
            ImGui.SetNextItemWidth(200f);
            changed |= ImGui.SliderFloat("##hitWarnSize", ref Config.HitWarningSize, 16f, 72f, "%.0f px");
            ImGui.SameLine();
            ImGui.Text("Text size");
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── Colors (collapsible, open by default) ─────────────────────────
        if (ImGui.CollapsingHeader("Colors", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawColorSettings(ref changed);
            ImGui.Unindent();
        }

        ImGui.EndTabItem();
    }

    private void DrawColorSettings(ref bool changed)
    {
        ImGui.Text("Cast start color:");
        ImGui.TextDisabled("  Color at the beginning of the cast.");
        changed |= ImGui.ColorEdit4("##start", ref Config.ColorStart);

        ImGui.Spacing();
        ImGui.Text("Cast end color:");
        ImGui.TextDisabled("  Color when the cast is about to resolve.");
        changed |= ImGui.ColorEdit4("##end", ref Config.ColorEnd);

        ImGui.Spacing();
        ImGui.Text("Presets:");
        ImGui.SetNextItemWidth(210f);
        int active = DetectActivePreset(Config.ColorStart, Config.ColorEnd);
        if (ImGui.BeginCombo("##presets", PresetNames[active]))
        {
            for (int i = 1; i < PresetNames.Length; i++)
            {
                bool selected = active == i;
                if (ImGui.Selectable(PresetNames[i], selected))
                {
                    Config.ColorStart = PresetColors[i].Start;
                    Config.ColorEnd   = PresetColors[i].End;
                    changed = true;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();
        ImGui.Text("Opacity:");
        ImGui.TextDisabled("  Controls how strong the color tint appears on omens.");
        changed |= ImGui.SliderFloat("##opacity", ref Config.OmenOpacity, 0.05f, 1.0f, "%.2f");

    }

    // ── Hitbox ───────────────────────────────────────────────────────────────
    private void DrawHitboxTab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("Hitbox")) return;

        // ── Position Dot ──────────────────────────────────────────────────
        ImGui.Text("Position Marker:");
        ImGui.TextDisabled("  Your AoE hitbox is a single point at your exact position.");
        ImGui.TextDisabled("  This dot shows where the game checks if you are hit.");
        ImGui.Spacing();

        changed |= ImGui.Checkbox("Show position dot", ref Config.ShowHitbox);

        if (Config.ShowHitbox)
        {
            ImGui.Indent();

            changed |= ImGui.Checkbox("Only show in combat", ref Config.HitboxOnlyInCombat);

            changed |= ImGui.ColorEdit4("##dotColor", ref Config.HitboxColor,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
            ImGui.SameLine();
            ImGui.Text("Dot color");

            changed |= ImGui.SliderFloat("##dotSize", ref Config.HitboxDotSize, 2f, 12f, "%.0f px");
            ImGui.SameLine();
            ImGui.Text("Dot size");

            changed |= ImGui.Checkbox("Dark outline", ref Config.HitboxOutline);
            if (Config.HitboxOutline)
            {
                ImGui.SameLine();
                changed |= ImGui.ColorEdit4("##outlineColor", ref Config.HitboxOutlineColor,
                    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
            }

            ImGui.Unindent();
        }

        // ── Ring 1 ────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Ring 1:");
        ImGui.TextDisabled("  Draws a ground ring around you at a custom radius (yalms).");
        ImGui.Spacing();

        changed |= ImGui.Checkbox("Show Ring 1", ref Config.ShowHitboxRing);

        if (Config.ShowHitboxRing)
        {
            ImGui.Indent();

            changed |= ImGui.SliderFloat("##ring1Radius", ref Config.HitboxRingRadius, 1f, 30f, "%.1f y");
            ImGui.SameLine();
            ImGui.Text("Radius");

            changed |= ImGui.ColorEdit4("##ring1Color", ref Config.HitboxRingColor,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
            ImGui.SameLine();
            ImGui.Text("Color");

            changed |= ImGui.SliderFloat("##ring1Thick", ref Config.HitboxRingThickness, 1f, 5f, "%.1f");
            ImGui.SameLine();
            ImGui.Text("Thickness");

            ImGui.Unindent();
        }

        // ── Ring 2 ────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Ring 2:");
        ImGui.TextDisabled("  A second ground ring for range reference.");
        ImGui.Spacing();

        changed |= ImGui.Checkbox("Show Ring 2", ref Config.ShowHitboxRing2);

        if (Config.ShowHitboxRing2)
        {
            ImGui.Indent();

            changed |= ImGui.SliderFloat("##ring2Radius", ref Config.HitboxRingRadius2, 1f, 50f, "%.1f y");
            ImGui.SameLine();
            ImGui.Text("Radius");

            changed |= ImGui.ColorEdit4("##ring2Color", ref Config.HitboxRingColor2,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
            ImGui.SameLine();
            ImGui.Text("Color");

            changed |= ImGui.SliderFloat("##ring2Thick", ref Config.HitboxRingThickness2, 1f, 5f, "%.1f");
            ImGui.SameLine();
            ImGui.Text("Thickness");

            ImGui.Unindent();
        }

        ImGui.EndTabItem();
    }

    // ── Shapes ───────────────────────────────────────────────────────────────
    private void DrawShapesTab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("Shapes")) return;

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Coming Soon");
        ImGui.TextDisabled("Shape filters for custom AoE indicators will be available in a future update.");

        ImGui.Spacing();

        // Grey out all checkboxes — display only, not interactive
        ImGui.BeginDisabled();
        bool circles = Config.ShowCircles;
        bool cones   = Config.ShowCones;
        bool rects   = Config.ShowRects;
        bool donuts  = Config.ShowDonuts;
        bool crosses = Config.ShowCrosses;
        ImGui.Checkbox("Circles",    ref circles);
        ImGui.Checkbox("Cones",      ref cones);
        ImGui.Checkbox("Rectangles", ref rects);
        ImGui.Checkbox("Donuts",     ref donuts);
        ImGui.Checkbox("Crosses",    ref crosses);
        ImGui.Separator();
        bool unavoidable = Config.HideUnavoidable;
        ImGui.Checkbox("Hide unavoidable (raidwide) attacks", ref unavoidable);
        ImGui.EndDisabled();

        ImGui.EndTabItem();
    }

    // ── Performance ──────────────────────────────────────────────────────────
    private void DrawPerformanceTab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("Performance")) return;

        int casts     = _plugin._scanner.LastScanCastCount;
        int entities  = _plugin._scanner.LastScanEntityCount;
        int recolored = _plugin._omenManager.LastRecolorCount;

        ImGui.Text("Omen recoloring:");
        ImGui.Separator();
        ImGui.Text($"  Omens recolored this frame: {recolored}");
        ImGui.Text($"  Active casts: {casts}");

        ImGui.Spacing();
        ImGui.Text("Entity scan:");
        ImGui.Separator();
        ImGui.Text($"  Objects iterated: {entities}");

        ImGui.EndTabItem();
    }

    // ── Preset helpers ────────────────────────────────────────────────────────

    private static int DetectActivePreset(Vector4 start, Vector4 end)
    {
        for (int i = 1; i < PresetColors.Length; i++)
        {
            if (ColorsMatch(start, PresetColors[i].Start) &&
                ColorsMatch(end,   PresetColors[i].End))
                return i;
        }
        return 0; // Custom
    }

    private static bool ColorsMatch(Vector4 a, Vector4 b)
        => MathF.Abs(a.X - b.X) < 0.01f && MathF.Abs(a.Y - b.Y) < 0.01f &&
           MathF.Abs(a.Z - b.Z) < 0.01f && MathF.Abs(a.W - b.W) < 0.01f;
}
