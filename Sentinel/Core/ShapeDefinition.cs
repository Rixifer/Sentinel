using System.Numerics;

namespace Sentinel.Core;

// ShapeType and ShapeDefinition kept for Phase 2 (spawning omens for untelegraphed mechanics).
// Not used in the Phase 1 hot path.

public enum ShapeType
{
    Circle,
    Cone,
    Rect,
    Donut,
    Cross,
}

public record struct ShapeDefinition(
    ShapeType Type,
    Vector3   Origin,
    float     Heading,
    float     Radius,
    float     InnerRadius,
    float     Range,
    float     HalfWidth,
    float     HalfAngle,
    bool      Unavoidable,
    bool      IsGroundTargeted,
    byte      CastType,
    uint      OmenId = 0
);

/// <summary>
/// Represents an active enemy cast tracked this frame.
/// Cast detection is driven by network events (ActorCast IPC packet).
/// ObjectTable is still iterated for VfxContainer reads and position updates.
/// </summary>
public record struct ActiveCast(
    ulong   EntityId,
    uint    ActionId,
    string  ActionName,
    float   Progress,          // 0..1
    bool    HasOmen,           // has any indicator — native game omen or custom-spawned
    uint    OmenId,            // from Lumina Action.Omen (0 = untelegraphed)
    Vector3 CasterPosition,    // for debug display
    bool    IsGroundTargeted,  // for debug display
    byte    CastType,          // for debug display / filtering
    // ── Network-sourced fields (from ActorCast IPC packet) ────────────────
    long    StartTimeTicks,    // Environment.TickCount64 when cast was first detected
    float   TotalCastTime,     // CastTime from the ActorCast packet (seconds)
    float   Heading,           // rotation in radians (-π to +π) from the packet
    Vector3 TargetPosition,    // decoded target position from the packet
    // ── Debug metadata ────────────────────────────────────────────────────
    string  DetectionSource,   // "NET" = ActorCast network packet
    string  IndicatorType,     // "NATIVE", "CUSTOM:BMR", "CUSTOM:LUM", "NONE"
    string  ShapeInfo          // "" for native; "Circle(8.0)" etc. for custom
);
