using Dalamud.Plugin.Services;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using LuminaOmen = Lumina.Excel.Sheets.Omen;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Sentinel.Core;

public class OmenSheetReader
{
    private readonly IDataManager _dataManager;

    // Omen RowId -> VFX path string
    private readonly Dictionary<uint, string> _omenPaths = new();

    // Standard omen RowId -> Enhanced (er_) omen RowId
    private readonly Dictionary<uint, uint> _enhancedRemap = new();

    // Enhanced omen RowId -> Standard omen RowId (reverse of _enhancedRemap)
    private readonly Dictionary<uint, uint> _standardRemap = new();

    // Action RowId -> Omen RowId (cached lookups)
    private readonly Dictionary<uint, uint> _actionOmenMap = new();

    public IReadOnlyDictionary<uint, string> OmenPaths => _omenPaths;
    public IReadOnlyDictionary<uint, uint> EnhancedRemap => _enhancedRemap;

    public OmenSheetReader(IDataManager dataManager)
    {
        _dataManager = dataManager;
        LoadOmenSheet();
    }

    private void LoadOmenSheet()
    {
        var omenSheet = _dataManager.GetExcelSheet<LuminaOmen>();
        if (omenSheet == null)
        {
            Plugin.Log.Warning("[Sentinel][OMEN_SHEET] Could not load Omen sheet.");
            return;
        }

        int count = 0;
        bool discoveredFields = false;

        foreach (var row in omenSheet)
        {
            try
            {
                string path = row.Path.ToString();
                if (!string.IsNullOrEmpty(path))
                {
                    _omenPaths[row.RowId] = path;
                    count++;
                }
            }
            catch
            {
                // If Path doesn't exist on this Lumina version, discover actual fields
                if (!discoveredFields)
                {
                    LogOmenFields(row);
                    discoveredFields = true;
                }
            }
        }

        Plugin.Log.Information("[Sentinel][OMEN_SHEET] Loaded {Count} omen entries.", count);

        BuildEnhancedRemap();
    }

    /// <summary>
    /// Classifies an omen VFX path into a shape category string.
    /// Standard and enhanced variants of the same shape produce the same category.
    /// Returns null for unrecognized paths (monster-specific, impact effects, etc.).
    /// </summary>
    private static string? ClassifyOmenShape(string path)
    {
        // Strip er_ prefix for classification
        string s = path.StartsWith("er_") ? path[3..] : path;

        // Cone: gl_fan{NNN}
        var fanMatch = Regex.Match(s, @"gl_fan(\d+)");
        if (fanMatch.Success)
            return $"fan{fanMatch.Groups[1].Value}";

        // Donut: gl_sircle_{NNNN}
        var sircleMatch = Regex.Match(s, @"gl_sircle_(\d+)");
        if (sircleMatch.Success)
            return $"sircle{sircleMatch.Groups[1].Value}";

        // Circle: general_1... (distinguishes from general02 and general_x02)
        if (Regex.IsMatch(s, @"^general_1"))
            return "circle";

        // Line: general02
        if (s.StartsWith("general02"))
            return "line";

        // Rectangle: general_x02
        if (s.StartsWith("general_x02"))
            return "rect";

        return null;
    }

    private void BuildEnhancedRemap()
    {
        var enhancedByCategory = new Dictionary<string, uint>();
        var standardByCategory = new Dictionary<string, List<uint>>();

        foreach (var (id, path) in _omenPaths)
        {
            string? category = ClassifyOmenShape(path);
            if (category == null) continue;

            if (path.StartsWith("er_"))
            {
                enhancedByCategory[category] = id;
            }
            else
            {
                if (!standardByCategory.TryGetValue(category, out var list))
                    standardByCategory[category] = list = new List<uint>();
                list.Add(id);
            }
        }

        foreach (var (category, enhancedId) in enhancedByCategory)
        {
            if (standardByCategory.TryGetValue(category, out var standardIds))
                foreach (var stdId in standardIds)
                    _enhancedRemap[stdId] = enhancedId;
        }

        Plugin.Log.Information("[Sentinel][OMEN_SHEET] Built {Count} enhanced omen remappings.", _enhancedRemap.Count);

        // Build reverse remap: enhanced → first standard match
        foreach (var (stdId, enhId) in _enhancedRemap)
        {
            if (!_standardRemap.ContainsKey(enhId))
                _standardRemap[enhId] = stdId;
        }
        Plugin.Log.Information("[Sentinel][OMEN_SHEET] Built {Count} standard omen reverse remappings.", _standardRemap.Count);

        foreach (var (stdId, enhId) in _enhancedRemap)
            Plugin.Log.Debug("[Sentinel][OMEN_REMAP] {StdId} ({StdPath}) → {EnhId} ({EnhPath})",
                stdId, _omenPaths.GetValueOrDefault(stdId, "?"),
                enhId, _omenPaths.GetValueOrDefault(enhId, "?"));

        foreach (var (category, enhId) in enhancedByCategory)
        {
            if (!standardByCategory.ContainsKey(category))
                Plugin.Log.Debug("[Sentinel][OMEN_REMAP] Unmatched enhanced: {Id} ({Path}) category={Cat}",
                    enhId, _omenPaths.GetValueOrDefault(enhId, "?"), category);
        }

        foreach (var (category, stdIds) in standardByCategory)
        {
            if (!enhancedByCategory.ContainsKey(category))
                foreach (var stdId in stdIds)
                    Plugin.Log.Debug("[Sentinel][OMEN_REMAP] No enhanced version for: {Id} ({Path}) category={Cat}",
                        stdId, _omenPaths.GetValueOrDefault(stdId, "?"), category);
        }
    }

    private static void LogOmenFields(object row)
    {
        var type = row.GetType();
        var props = type.GetProperties();
        var fields = type.GetFields();

        Plugin.Log.Warning("[Sentinel][OMEN_SHEET] Omen type has {Pc} properties, {Fc} fields:",
            props.Length, fields.Length);
        foreach (var p in props)
            Plugin.Log.Warning("[Sentinel][OMEN_SHEET]   prop: {Name} ({Type})", p.Name, p.PropertyType.Name);
        foreach (var f in fields)
            Plugin.Log.Warning("[Sentinel][OMEN_SHEET]   field: {Name} ({Type})", f.Name, f.FieldType.Name);
    }

    /// <summary>
    /// Look up an action's Omen RowId. Caches results.
    /// Returns 0 if the action has no omen.
    /// </summary>
    public uint GetActionOmenId(uint actionId)
    {
        if (_actionOmenMap.TryGetValue(actionId, out var cached))
            return cached;

        uint omenId = 0;
        var sheet = _dataManager.GetExcelSheet<LuminaAction>();
        var row = sheet?.GetRowOrDefault(actionId);
        if (row.HasValue)
        {
            try
            {
                omenId = row.Value.Omen.RowId;
            }
            catch { /* Lumina version doesn't expose Omen as RowRef */ }
        }

        _actionOmenMap[actionId] = omenId;
        return omenId;
    }

    /// <summary>
    /// Get the enhanced (er_) RowId for a standard omen. Returns null if no enhanced version exists.
    /// </summary>
    public uint? GetEnhancedOmenId(uint standardOmenId)
        => _enhancedRemap.TryGetValue(standardOmenId, out var enhanced) ? enhanced : null;

    /// <summary>
    /// Get the standard (non-er_) RowId for an enhanced omen. Returns null if already standard or no match.
    /// </summary>
    public uint? GetStandardOmenId(uint enhancedOmenId)
        => _standardRemap.TryGetValue(enhancedOmenId, out var standard) ? standard : null;

    /// <summary>
    /// Get the VFX path for an omen ID. Returns null if unknown.
    /// </summary>
    public string? GetOmenPath(uint omenId)
    {
        return _omenPaths.TryGetValue(omenId, out var path) ? path : null;
    }
}
