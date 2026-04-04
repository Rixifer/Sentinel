using System.Numerics;
using System.Runtime.InteropServices;

namespace Sentinel.Structs;

/// <summary>
/// VFX omen object layout. Mapped from GoodOmen + Pictomancy research.
/// FFXIVClientStructs declares this struct but has no fields mapped.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x1E0)]
public unsafe struct VfxOmenData
{
    [FieldOffset(0x1B8)] public VfxOmenResourceInstance* Instance;
}

/// <summary>
/// Per-instance resource data for a VFX omen.
/// Mapped from GoodOmen (Color) + Pictomancy (Scale, Color).
/// FFXIVClientStructs has this struct but only maps 0x08 (VfxResourceUnk).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xC0)]
public unsafe struct VfxOmenResourceInstance
{
    [FieldOffset(0x90)] public Vector3 Scale;
    [FieldOffset(0xA0)] public Vector4 Color;
}
