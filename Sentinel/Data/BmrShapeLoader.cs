using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Sentinel.Data;

public class BmrShapeLoader
{
    private readonly Dictionary<uint, BmrShapeEntry> _shapes          = new();
    private readonly Dictionary<string, int>         _shapeTypeCounts = new();

    public IReadOnlyDictionary<uint, BmrShapeEntry> Shapes          => _shapes;
    public IReadOnlyDictionary<string, int>         ShapeTypeCounts  => _shapeTypeCounts;

    public BmrShapeLoader(string pluginDir)
    {
        Load(pluginDir);
    }

    private void Load(string pluginDir)
    {
        var path = Path.Combine(pluginDir, "Data", "BmrShapes.json");
        if (!File.Exists(path))
        {
            Plugin.Log.Warning("[Sentinel] BmrShapes.json not found at {Path}.", path);
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var entries = JsonSerializer.Deserialize<List<BmrShapeEntry>>(json, options);
            if (entries == null) return;

            foreach (var entry in entries)
                _shapes.TryAdd(entry.ActionId, entry); // first entry wins for duplicates

            // Tally shape types across the loaded (de-duped) entries
            foreach (var e in _shapes.Values)
                _shapeTypeCounts[e.ShapeType] =
                    _shapeTypeCounts.TryGetValue(e.ShapeType, out var c) ? c + 1 : 1;

            Plugin.Log.Information("[Sentinel] Loaded {Count} BMR shape entries.", _shapes.Count);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error("[Sentinel] Failed to load BmrShapes.json: {Msg}", ex.Message);
        }
    }

    public BmrShapeEntry? GetShape(uint actionId)
        => _shapes.TryGetValue(actionId, out var entry) ? entry : null;
}

public class BmrShapeEntry
{
    public uint   ActionId     { get; set; }
    public string ActionName   { get; set; } = "";
    public string ShapeType    { get; set; } = "";
    public float  Radius       { get; set; }
    public float  HalfAngleDeg { get; set; }
    public float  HalfWidth    { get; set; }
    public float  InnerRadius  { get; set; }
    public float  OuterRadius  { get; set; }
    public float  LengthFront  { get; set; }
    public float  LengthBack   { get; set; }
    public string Source       { get; set; } = "";
    public bool   Duplicate    { get; set; }
}
