using Dalamud.Plugin.Services;
using System;
using System.Runtime.InteropServices;

namespace Sentinel.Core;

/// <summary>
/// Resolved game function pointers for the VFX system.
/// All signatures from Pictomancy's VfxFunctions.cs (confirmed working).
/// Call Initialize() once at plugin startup with ISigScanner.
/// </summary>
public static unsafe class VfxFunctions
{
    public const string UpdateVfxColorSig = "E8 ?? ?? ?? ?? 8B 4B F3";
    public delegate long UpdateVfxColorDelegate(nint vfxResourceInstance, float r, float g, float b, float a);
    public static UpdateVfxColorDelegate? UpdateVfxColor;

    public const string VfxInitDataCtorSig = "E8 ?? ?? ?? ?? 8D 57 06 48 8D 4C 24 ??";
    public delegate nint VfxInitDataCtorDelegate(nint self);
    public static VfxInitDataCtorDelegate? VfxInitDataCtor;

    public const string CreateVfxSig = "E8 ?? ?? ?? ?? 48 8B D8 48 8D 95";
    public delegate nint CreateVfxDelegate(
        byte* path, nint init, byte a3, byte a4,
        float originX, float originY, float originZ,
        float sizeX, float sizeY, float sizeZ,
        float angle, float duration, int a13);
    public static CreateVfxDelegate? CreateVfx;

    public const string DestroyVfxSig = "E8 ?? ?? ?? ?? 4D 89 A4 DE ?? ?? ?? ??";
    public delegate void DestroyVfxDelegate(nint vfxData);
    public static DestroyVfxDelegate? DestroyVfx;

    public const string UpdateVfxTransformSig = "E8 ?? ?? ?? ?? EB 19 48 8B 0B";
    public delegate long UpdateVfxTransformDelegate(nint vfxResourceInstance, nint matrix);
    public static UpdateVfxTransformDelegate? UpdateVfxTransform;

    public const string RotateMatrixSig = "E8 ?? ?? ?? ?? 4C 8D 76 20";
    public delegate void RotateMatrixDelegate(nint matrix, float rotation);
    public static RotateMatrixDelegate? RotateMatrix;

    // ── Resolution status ──────────────────────────────────────────────────
    public static bool IsFullyResolved     { get; private set; }
    public static int  ResolvedCount       { get; private set; }

    public static bool HasUpdateVfxColor    => UpdateVfxColor    != null;
    public static bool HasVfxInitDataCtor   => VfxInitDataCtor   != null;
    public static bool HasCreateVfx         => CreateVfx         != null;
    public static bool HasDestroyVfx        => DestroyVfx        != null;
    public static bool HasUpdateVfxTransform=> UpdateVfxTransform!= null;
    public static bool HasRotateMatrix      => RotateMatrix      != null;

    // ── Resolved addresses (nint.Zero if failed) ───────────────────────────
    // Properties backed by private static fields so they can be passed as `out` params.
    private static nint _addrUpdateVfxColor;
    private static nint _addrVfxInitDataCtor;
    private static nint _addrCreateVfx;
    private static nint _addrDestroyVfx;
    private static nint _addrUpdateVfxTransform;
    private static nint _addrRotateMatrix;

    public static nint AddrUpdateVfxColor    => _addrUpdateVfxColor;
    public static nint AddrVfxInitDataCtor   => _addrVfxInitDataCtor;
    public static nint AddrCreateVfx         => _addrCreateVfx;
    public static nint AddrDestroyVfx        => _addrDestroyVfx;
    public static nint AddrUpdateVfxTransform=> _addrUpdateVfxTransform;
    public static nint AddrRotateMatrix      => _addrRotateMatrix;

    public static void Initialize(ISigScanner sigScanner)
    {
        int resolved = 0;
        const int total = 6;

        UpdateVfxColor     = TryResolve<UpdateVfxColorDelegate>   (sigScanner, UpdateVfxColorSig,    "UpdateVfxColor",    ref resolved, out _addrUpdateVfxColor);
        VfxInitDataCtor    = TryResolve<VfxInitDataCtorDelegate>  (sigScanner, VfxInitDataCtorSig,   "VfxInitDataCtor",   ref resolved, out _addrVfxInitDataCtor);
        CreateVfx          = TryResolve<CreateVfxDelegate>        (sigScanner, CreateVfxSig,         "CreateVfx",         ref resolved, out _addrCreateVfx);
        DestroyVfx         = TryResolve<DestroyVfxDelegate>       (sigScanner, DestroyVfxSig,        "DestroyVfx",        ref resolved, out _addrDestroyVfx);
        UpdateVfxTransform = TryResolve<UpdateVfxTransformDelegate>(sigScanner, UpdateVfxTransformSig,"UpdateVfxTransform",ref resolved, out _addrUpdateVfxTransform);
        RotateMatrix       = TryResolve<RotateMatrixDelegate>     (sigScanner, RotateMatrixSig,      "RotateMatrix",      ref resolved, out _addrRotateMatrix);

        ResolvedCount   = resolved;
        IsFullyResolved = resolved == total;
        Plugin.Log.Information("[Sentinel] VfxFunctions: resolved {Resolved}/{Total} game functions.", resolved, total);
    }

    private static T? TryResolve<T>(ISigScanner scanner, string sig, string name, ref int count, out nint addr)
        where T : Delegate
    {
        addr = nint.Zero;
        try
        {
            addr = scanner.ScanText(sig);
            var del = Marshal.GetDelegateForFunctionPointer<T>(addr);
            Plugin.Log.Information("[Sentinel] {Name} resolved at 0x{Addr:X}.", name, addr);
            count++;
            return del;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning("[Sentinel] {Name} sig scan failed: {Msg}", name, ex.Message);
            return null;
        }
    }
}
