using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Sentinel.Core;

/// <summary>
/// Per-instance wrapper for a custom-spawned free-standing VFX omen.
/// Owns the game VFX object and its aligned matrix buffer.
/// Modeled on Pictomancy's Vfx.cs.
/// </summary>
public unsafe class OmenVfx : IDisposable
{
    private nint _vfxData;
    private nint _alignedRaw;
    private nint _alignedMatrix;

    public string  Path     { get; private set; }
    public Vector3 Position { get; private set; }
    public Vector3 Size     { get; private set; }
    public float   Rotation { get; private set; }
    public Vector4 Color    { get; private set; }

    public bool IsValid =>
        _vfxData != nint.Zero && InstancePtr != nint.Zero;

    /// <summary>
    /// Reads Instance* at VfxData+0x1B8.
    /// Returns nint.Zero if _vfxData is zero or the pointer stored there is null.
    /// </summary>
    private nint InstancePtr
    {
        get
        {
            if (_vfxData == nint.Zero) return nint.Zero;
            return *(nint*)(_vfxData + 0x1B8);
        }
    }

    private OmenVfx(string path, nint vfxData, nint alignedRaw, nint alignedMatrix,
                    Vector3 position, Vector3 size, float rotation, Vector4 color)
    {
        Path           = path;
        _vfxData       = vfxData;
        _alignedRaw    = alignedRaw;
        _alignedMatrix = alignedMatrix;
        Position       = position;
        Size           = size;
        Rotation       = rotation;
        Color          = color;
    }

    /// <summary>
    /// Creates a new free-standing VFX omen. Returns null if required game functions are not resolved
    /// or if the game returns a null VfxData pointer.
    /// </summary>
    public static OmenVfx? Create(string avfxPath, Vector3 position, Vector3 size, float rotation, Vector4 color)
    {
        if (VfxFunctions.CreateVfx == null || VfxFunctions.VfxInitDataCtor == null)
            return null;

        // Allocate the 16-byte-aligned matrix buffer once per instance.
        nint alignedRaw    = Marshal.AllocHGlobal(64 + 8);
        nint alignedMatrix = new nint(16 * (((long)alignedRaw + 15) / 16));

        // Stack-allocate and constructor-initialize VfxInitData (opaque 0x1A0-byte block).
        byte* initBuffer = stackalloc byte[0x1A0];
        nint  initPtr    = (nint)initBuffer;
        VfxFunctions.VfxInitDataCtor!(initPtr);

        var pathBytes = Encoding.UTF8.GetBytes(avfxPath + '\0');
        fixed (byte* pathPtr = pathBytes)
        {
            nint vfxData = VfxFunctions.CreateVfx!(pathPtr, initPtr, 2, 0,
                position.X, position.Y, position.Z,
                size.X, size.Y, size.Z,
                rotation, 1f, -1);

            if (vfxData == nint.Zero)
            {
                Marshal.FreeHGlobal(alignedRaw);
                return null;
            }

            var instance = new OmenVfx(avfxPath, vfxData, alignedRaw, alignedMatrix,
                                       position, size, rotation, color);

            // Direct color write at creation time — engine has not yet snapshotted emitters.
            nint instPtr = instance.InstancePtr;
            if (instPtr != nint.Zero)
                *(Vector4*)((byte*)instPtr + 0xA0) = color;

            // Set the transform matrix.
            instance.UpdateTransform(position, size, rotation);

            return instance;
        }
    }

    /// <summary>
    /// Rebuilds the SRT matrix and calls UpdateVfxTransform to move/resize the VFX.
    /// </summary>
    public void UpdateTransform(Vector3 position, Vector3 size, float rotation)
    {
        Position = position;
        Size     = size;
        Rotation = rotation;

        // Build scale matrix, rotate in-place, then set translation row.
        // The matrix must be 16-byte aligned (SSE requirement in UpdateVfxTransform).
        var m = (float*)_alignedMatrix;
        m[0]  = size.X; m[1]  = 0;      m[2]  = 0;      m[3]  = 0;
        m[4]  = 0;      m[5]  = size.Y; m[6]  = 0;      m[7]  = 0;
        m[8]  = 0;      m[9]  = 0;      m[10] = size.Z; m[11] = 0;

        // Apply rotation BEFORE writing translation (matches Pictomancy's Vfx.UpdateTransform).
        if (VfxFunctions.RotateMatrix != null)
            VfxFunctions.RotateMatrix(_alignedMatrix, rotation);

        m[12] = position.X;
        m[13] = position.Y;
        m[14] = position.Z;
        m[15] = 0;

        nint instPtr = InstancePtr;
        if (instPtr != nint.Zero && VfxFunctions.UpdateVfxTransform != null)
            VfxFunctions.UpdateVfxTransform(instPtr, _alignedMatrix);
    }

    /// <summary>
    /// Updates the VFX color. Uses the game's UpdateVfxColor function when available;
    /// falls back to a direct memory write at Instance+0xA0.
    /// </summary>
    public void UpdateColor(Vector4 color)
    {
        Color = color;

        nint instPtr = InstancePtr;
        if (instPtr == nint.Zero) return;

        if (VfxFunctions.UpdateVfxColor != null)
            VfxFunctions.UpdateVfxColor(instPtr, color.X, color.Y, color.Z, color.W);
        else
            *(Vector4*)((byte*)instPtr + 0xA0) = color;
    }

    public void Dispose()
    {
        if (_vfxData != nint.Zero)
        {
            if (VfxFunctions.DestroyVfx != null)
                VfxFunctions.DestroyVfx(_vfxData);
            _vfxData = nint.Zero;
        }

        if (_alignedRaw != nint.Zero)
        {
            Marshal.FreeHGlobal(_alignedRaw);
            _alignedRaw    = nint.Zero;
            _alignedMatrix = nint.Zero;
        }
    }
}
