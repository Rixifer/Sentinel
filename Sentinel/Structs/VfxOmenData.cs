using System.Numerics;
using System.Runtime.InteropServices;

namespace Sentinel.Structs;

/// <summary>
/// Represents a VFX omen object. Offset 0x1B8 points to the resource instance
/// that controls visual properties such as color.
/// Struct layout mirrors GoodOmen's VfxData.cs.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public unsafe struct VfxOmenData
{
    [FieldOffset(0x1B8)] public VfxOmenResourceInstance* Instance;
}

/// <summary>
/// Represents the per-instance resource data for a VFX omen.
/// Offset 0xA0 is the RGBA color (Vector4). Setting alpha to 0 makes the omen invisible.
/// Struct layout mirrors GoodOmen's VfxResourceInstance.cs.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public unsafe struct VfxOmenResourceInstance
{
    [FieldOffset(0xA0)] public Vector4 Color;
}
