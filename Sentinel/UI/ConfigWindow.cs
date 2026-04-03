using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace Sentinel.UI;

public class ConfigWindow : Window
{
    private readonly Plugin _plugin;
    private Configuration Config => _plugin.Config;

    // Consistent label width for aligned controls
    private const float LabelWidth = 115f;

    private static readonly string[] PresetNames =
    {
        "Custom",
        "Default (Orange)",
        "Deuteranopia (Blue)",
        "Protanopia (Cyan)",
        "Tritanopia (Pink)",
    };

    private static readonly Vector4[] PresetColors =
    {
        default,                               // 0 Custom (unused)
        new(1.0f, 0.545f, 0.239f, 1.0f),      // 1 Orange
        new(0.0f, 0.4f, 1.0f, 1.0f),          // 2 Blue
        new(0.0f, 0.9f, 1.0f, 1.0f),          // 3 Cyan
        new(1.0f, 0.4f, 0.8f, 1.0f),          // 4 Pink
    };

    public ConfigWindow(Plugin plugin) : base("Sentinel Settings v0.4.0.0###SentinelConfig")
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(700, 700),
        };
    }

    public override void Draw()
    {
        bool changed = false;

        // ── Enable / Disable (centered) ──────────────────────────────────────
        {
            bool enabled = Config.Enabled;
            string btnText = enabled ? "  ENABLED  " : "  DISABLED  ";
            float btnWidth = ImGui.CalcTextSize(btnText).X + ImGui.GetStyle().FramePadding.X * 2f;
            float windowWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX((windowWidth - btnWidth) * 0.5f);

            ImGui.PushStyleColor(ImGuiCol.Button, enabled
                ? new Vector4(0.15f, 0.55f, 0.15f, 1.0f)
                : new Vector4(0.45f, 0.10f, 0.10f, 1.0f));
            if (ImGui.Button(btnText))
            {
                Config.Enabled = !Config.Enabled;
                changed = true;
            }
            ImGui.PopStyleColor();
        }

        ImGui.Separator();
        ImGui.Spacing();

        // ── Tabs ─────────────────────────────────────────────────────────────
        if (ImGui.BeginTabBar("SentinelTabs"))
        {
            DrawAoETab(ref changed);
            DrawHitboxTab(ref changed);
            DrawShapesTab(ref changed);
            ImGui.EndTabBar();
        }

        if (changed) Config.Save();
    }

    /// <summary>Draws a right-aligned label then positions cursor for the next control.</summary>
    private static void Label(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(text);
        ImGui.SameLine(LabelWidth);
    }

    // ── AoE Tab ───────────────────────────────────────────────────────────────
    private void DrawAoETab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("AoE")) return;

        // ── AoE ───────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("AoE", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();

            Label("AoE Style:");
            string[] styleNames = { "Default", "Force Enhanced (Dawntrail)", "Force Legacy" };
            string[] styleTips =
            {
                "Uses whichever indicator style the game assigns.",
                "Upgrades all indicators to the Dawntrail style with light wall borders.",
                "Uses the original flat ground indicators.",
            };
            int style = (int)Config.IndicatorStyle;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##omenStyle", styleNames[style]))
            {
                for (int i = 0; i < styleNames.Length; i++)
                {
                    bool selected = style == i;
                    if (ImGui.Selectable(styleNames[i], selected))
                    {
                        Config.IndicatorStyle = (OmenStyle)i;
                        changed = true;
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(styleTips[i]);
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.Spacing();
            changed |= ImGui.Checkbox("Warn when standing in AoE", ref Config.ShowHitWarning);
            ImGui.TextDisabled("  Shows a warning symbol above your character.");
            if (Config.ShowHitWarning)
            {
                Label("Color:");
                changed |= ImGui.ColorEdit4("##hitWarnColor", ref Config.HitWarningColor,
                    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);

                Label("Size:");
                ImGui.SetNextItemWidth(-1f);
                changed |= ImGui.SliderFloat("##hitWarnSize", ref Config.HitWarningSize, 16f, 72f, "%.0f px");
            }

            ImGui.Unindent();
        }

        ImGui.Spacing();

        // ── Colors ────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Colors", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawColorSettings(ref changed);
            ImGui.Unindent();
        }

        ImGui.Spacing();

        // ── Cast Bar ──────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Enemy Cast Bar", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawCastBarSettings(ref changed);
            ImGui.Unindent();
        }

        ImGui.EndTabItem();
    }

    private void DrawColorSettings(ref bool changed)
    {
        // Omen Color: [picker] [preset dropdown]
        Label("Omen Color:");
        changed |= ImGui.ColorEdit4("##omenColor", ref Config.OmenColor,
            ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        int active = DetectActivePreset(Config.OmenColor);
        if (ImGui.BeginCombo("##presets", PresetNames[active]))
        {
            for (int i = 1; i < PresetNames.Length; i++)
            {
                bool selected = active == i;
                if (ImGui.Selectable(PresetNames[i], selected))
                {
                    Config.OmenColor = PresetColors[i];
                    changed = true;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        // Opacity: [slider]
        Label("Opacity:");
        ImGui.SetNextItemWidth(-1f);
        changed |= ImGui.SliderFloat("##opacity", ref Config.OmenOpacity, 0.05f, 1.0f, "%.2f");

        // Intensity: [slider]
        Label("Intensity:");
        ImGui.SetNextItemWidth(-1f);
        changed |= ImGui.SliderFloat("##glowIntensity", ref Config.GlowIntensity, 0.5f, 2.0f, "%.2f");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pushes colors into HDR to trigger bloom. Sweet spot is 1.1\u20131.4.");
    }

    private void DrawCastBarSettings(ref bool changed)
    {
        changed |= ImGui.Checkbox("Show cast bar above AoEs", ref Config.ShowCastBar);
        ImGui.TextDisabled("  Displays a progress bar at the AoE center.");

        if (!Config.ShowCastBar) return;

        ImGui.Spacing();
        changed |= ImGui.Checkbox("Show action name", ref Config.ShowCastBarName);
        changed |= ImGui.Checkbox("Show remaining time", ref Config.ShowCastBarTime);

        // Fill color: [picker]
        ImGui.Spacing();
        Label("Fill Color:");
        changed |= ImGui.ColorEdit4("##castBarFill", ref Config.CastBarFillColor,
            ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);

        // Width / Height
        Label("Width:");
        ImGui.SetNextItemWidth(-1f);
        changed |= ImGui.SliderFloat("##castBarWidth", ref Config.CastBarWidth, 80f, 200f, "%.0f px");

        Label("Height:");
        ImGui.SetNextItemWidth(-1f);
        changed |= ImGui.SliderFloat("##castBarHeight", ref Config.CastBarHeight, 4f, 16f, "%.0f px");
    }

    // ── Hitbox Tab ────────────────────────────────────────────────────────────
    private void DrawHitboxTab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("Hitbox")) return;

        // ── Position Marker ───────────────────────────────────────────────
        changed |= ImGui.Checkbox("Show position dot", ref Config.ShowHitbox);
        ImGui.TextDisabled("  Shows a dot at your exact position \u2014 the point the game checks for AoE hits.");

        if (Config.ShowHitbox)
        {
            ImGui.Indent();

            changed |= ImGui.Checkbox("Only show in combat", ref Config.HitboxOnlyInCombat);

            Label("Color:");
            changed |= ImGui.ColorEdit4("##dotColor", ref Config.HitboxColor,
                ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f);
            changed |= ImGui.SliderFloat("##dotSize", ref Config.HitboxDotSize, 2f, 12f, "%.0f px");

            changed |= ImGui.Checkbox("Dark outline", ref Config.HitboxOutline);
            if (Config.HitboxOutline)
            {
                ImGui.SameLine();
                changed |= ImGui.ColorEdit4("##outlineColor", ref Config.HitboxOutlineColor,
                    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
            }

            ImGui.Unindent();
        }

        // ── Rings ─────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Rings:");
        ImGui.TextDisabled("  Ground rings at a custom radius.");
        ImGui.Spacing();

        DrawRingSettings("Ring 1", ref Config.ShowHitboxRing, ref Config.HitboxRingRadius,
            ref Config.HitboxRingColor, ref Config.HitboxRingThickness, 1f, 30f, "1", ref changed);

        DrawRingSettings("Ring 2", ref Config.ShowHitboxRing2, ref Config.HitboxRingRadius2,
            ref Config.HitboxRingColor2, ref Config.HitboxRingThickness2, 1f, 50f, "2", ref changed);

        DrawRingSettings("Ring 3", ref Config.ShowHitboxRing3, ref Config.HitboxRingRadius3,
            ref Config.HitboxRingColor3, ref Config.HitboxRingThickness3, 1f, 50f, "3", ref changed);

        ImGui.EndTabItem();
    }

    private static void DrawRingSettings(string label, ref bool show, ref float radius,
        ref Vector4 color, ref float thickness, float minR, float maxR, string id, ref bool changed)
    {
        changed |= ImGui.Checkbox($"Show {label}", ref show);
        if (!show) return;

        ImGui.Indent();
        ImGui.AlignTextToFramePadding();
        changed |= ImGui.ColorEdit4($"##ring{id}Color", ref color,
            ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreview);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        changed |= ImGui.SliderFloat($"##ring{id}Radius", ref radius, minR, maxR, "%.1f y");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        changed |= ImGui.SliderFloat($"##ring{id}Thick", ref thickness, 1f, 5f, "%.1f");
        ImGui.SameLine();
        ImGui.TextDisabled("thick");
        ImGui.Unindent();
    }

    // ── Shapes Tab ────────────────────────────────────────────────────────────
    private void DrawShapesTab(ref bool changed)
    {
        if (!ImGui.BeginTabItem("Shapes")) return;

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Coming Soon");
        ImGui.TextDisabled("Shape filters will be available in a future update.");

        ImGui.Spacing();

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

    // ── Preset helpers ────────────────────────────────────────────────────────

    private static int DetectActivePreset(Vector4 color)
    {
        for (int i = 1; i < PresetColors.Length; i++)
        {
            if (ColorsMatch(color, PresetColors[i]))
                return i;
        }
        return 0;
    }

    private static bool ColorsMatch(Vector4 a, Vector4 b)
        => MathF.Abs(a.X - b.X) < 0.01f && MathF.Abs(a.Y - b.Y) < 0.01f &&
           MathF.Abs(a.Z - b.Z) < 0.01f && MathF.Abs(a.W - b.W) < 0.01f;
}
