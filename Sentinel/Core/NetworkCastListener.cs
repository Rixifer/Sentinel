using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sentinel.Core;

// ─── Public event records ─────────────────────────────────────────────────────

/// <summary>
/// Fired when an ActorCast IPC packet is received.
/// EntityId is the SourceActor uint from the packet header, cast to ulong.
/// ActionId is the SpellID (ushort at offset 0x00) — stable, no scramble needed.
/// </summary>
public record struct CastStartEvent(
    ulong   EntityId,
    uint    ActionId,
    float   CastTime,   // seconds
    float   Rotation,   // radians, -π to +π
    float   TargetX,
    float   TargetY,
    float   TargetZ
);

/// <summary>
/// Fired when an ActorControl packet arrives with category CancelCast (15).
/// </summary>
public record struct CastCancelEvent(ulong EntityId, uint ActionId);

/// <summary>
/// Fired when any ActionEffect1/8/16/24/32 packet is received (cast resolved).
/// EntityId is SourceActor; ActionId is the ActionEffectHeader.actionId field
/// (SpellID ushort inside the action effect header at offset +0x0A within the header).
/// </summary>
public record struct ActionResolveEvent(ulong EntityId, uint ActionId);

// ─── Raw IPC structs ─────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Explicit, Pack = 1)]
internal unsafe struct RawIPCHeader
{
    [FieldOffset(0x00)] public ushort Magic;       // 0x0014
    [FieldOffset(0x02)] public ushort MessageType; // opcode
    [FieldOffset(0x04)] public uint   Unknown1;
    [FieldOffset(0x08)] public uint   Unknown2;
    [FieldOffset(0x0C)] public uint   Epoch;
    // payload starts at +0x10
}

[StructLayout(LayoutKind.Explicit, Pack = 1)]
internal unsafe struct RawIPCPacket
{
    [FieldOffset(0x20)] public uint   SourceActor;
    [FieldOffset(0x24)] public uint   TargetActor;
    [FieldOffset(0x30)] public ulong  PacketSize;
    [FieldOffset(0x38)] public RawIPCHeader* PacketData;
}

[StructLayout(LayoutKind.Explicit, Pack = 1)]
internal unsafe struct RawReceivedPacket
{
    [FieldOffset(0x10)] public RawIPCPacket* IPC;
    [FieldOffset(0x18)] public long          SendTimestamp;
}

/// <summary>
/// Hooks the game's FetchReceivedPacket function to intercept IPC packets.
/// Fires on the game thread (before Framework.Update), so we enqueue events
/// into lock-free concurrent queues for safe consumption on the update thread.
/// </summary>
public sealed unsafe class NetworkCastListener : IDisposable
{
    // ── Queues drained by CastScanner on Framework.Update ─────────────────
    public readonly ConcurrentQueue<CastStartEvent>     CastStarts     = new();
    public readonly ConcurrentQueue<CastCancelEvent>    CastCancels    = new();
    public readonly ConcurrentQueue<ActionResolveEvent> ActionResolves = new();

    // ── Debug stats ───────────────────────────────────────────────────────
    public bool IsActive           => _fetchHook?.IsEnabled ?? false;
    public long TotalCastStarts    { get; private set; }
    public long TotalActionResolves{ get; private set; }
    public long TotalCastCancels   { get; private set; }

    // ── Hook state ────────────────────────────────────────────────────────
    private delegate bool FetchReceivedPacketDelegate(void* self, RawReceivedPacket* outData);
    private readonly Hook<FetchReceivedPacketDelegate>? _fetchHook;

    // ── Runtime opcode map (populated at ctor, never changes per session) ─
    // We cache the live wire opcode for each packet type we care about.
    // On a patch the game binary changes and the plugin reloads, so caching
    // per session is safe.
    private readonly ushort _opcodeActorCast;
    private readonly ushort _opcodeActorControl;
    private readonly ushort _opcodeActionEffect1;
    private readonly ushort _opcodeActionEffect8;
    private readonly ushort _opcodeActionEffect16;
    private readonly ushort _opcodeActionEffect24;
    private readonly ushort _opcodeActionEffect32;

    // ── ActorControl category constant ────────────────────────────────────
    private const ushort CancelCastCategory = 15;

    // ── ActorCast field offsets inside the IPC payload ────────────────────
    // (payload starts at RawIPCHeader+0x10, i.e. the byte just after the header)
    // Struct layout (pack=1):
    //   +0x00 ushort SpellID
    //   +0x02 byte   ActionType
    //   +0x03 byte   BaseCastTime100ms
    //   +0x04 uint   ActionID (scrambled since 7.2 — we use SpellID instead)
    //   +0x08 float  CastTime
    //   +0x0C uint   TargetID
    //   +0x10 ushort Rotation
    //   +0x12 byte   Interruptible
    //   +0x13 byte   u1
    //   +0x14 uint   BallistaEntityId
    //   +0x18 ushort PosX
    //   +0x1A ushort PosY
    //   +0x1C ushort PosZ
    //   +0x1E ushort u3

    // ── ActorControl payload offsets ──────────────────────────────────────
    //   +0x00 ushort category
    //   +0x02 ushort padding
    //   +0x04 uint   param1   (for CancelCast: log message id)
    //   +0x08 uint   param2   (for CancelCast: ActionType)
    //   +0x0C uint   param3   (for CancelCast: ActionID — scrambled)
    //   +0x10 uint   param4   (for CancelCast: interrupted flag)

    // ── ActionEffectHeader field offsets inside payload ───────────────────
    //   +0x00 ulong  animationTargetId
    //   +0x08 uint   actionId  (scrambled)
    //   +0x0C uint   globalEffectCounter
    //   +0x10 float  animationLockTime
    //   +0x14 uint   BallistaEntityId
    //   +0x18 ushort SourceSequence
    //   +0x1A ushort rotation
    //   +0x1C ushort actionAnimationId
    //   +0x1E byte   variation
    //   +0x1F byte   actionType
    //   -- for action ID we use actionAnimationId (ushort at +0x1C) which
    //      matches SpellID/animationId and is not scrambled --
    //   Actually: we read the SpellID at +0x1C (actionAnimationId)
    //   which is the same as the SpellID in ActorCast.

    public NetworkCastListener(IGameInteropProvider interop, ISigScanner sigScanner)
    {
        // ── Step 1: Discover live opcodes via OpcodeMap ──────────────────
        // We reuse BMR's OpcodeMap approach: scan the IPC dispatch function,
        // walk its jump table, and record opcode→vtable-index mappings.
        // PacketID enum values (stable indices):
        //   ActorCast       = 280
        //   ActorControl    = 226
        //   ActionEffect1   = 230
        //   ActionEffect8   = 233
        //   ActionEffect16  = 234
        //   ActionEffect24  = 235
        //   ActionEffect32  = 236
        try
        {
            var opcodes = DiscoverOpcodes(sigScanner);
            _opcodeActorCast       = opcodes.ActorCast;
            _opcodeActorControl    = opcodes.ActorControl;
            _opcodeActionEffect1   = opcodes.ActionEffect1;
            _opcodeActionEffect8   = opcodes.ActionEffect8;
            _opcodeActionEffect16  = opcodes.ActionEffect16;
            _opcodeActionEffect24  = opcodes.ActionEffect24;
            _opcodeActionEffect32  = opcodes.ActionEffect32;

            Plugin.Log.Information(
                "[Sentinel][Net] Opcodes discovered — ActorCast=0x{AC:X4}, ActorControl=0x{ACC:X4}, " +
                "AE1=0x{AE1:X4}, AE8=0x{AE8:X4}, AE16=0x{AE16:X4}, AE24=0x{AE24:X4}, AE32=0x{AE32:X4}",
                _opcodeActorCast, _opcodeActorControl,
                _opcodeActionEffect1, _opcodeActionEffect8,
                _opcodeActionEffect16, _opcodeActionEffect24, _opcodeActionEffect32);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(
                "[Sentinel][Net] Opcode discovery failed: {Msg}. NetworkCastListener will be inactive.", ex.Message);
            return;
        }

        // ── Step 2: Hook FetchReceivedPacket ──────────────────────────────
        // Same two-sig fallback pattern as BMR's PacketInterceptor.
        bool found =
            sigScanner.TryScanText("E8 ?? ?? ?? ?? 84 C0 0F 85 ?? ?? ?? ?? 48 8D 4C 24 ?? FF 15", out var fetchAddr)
            || sigScanner.TryScanText("E8 ?? ?? ?? ?? 84 C0 0F 85 ?? ?? ?? ?? 44 0F B6 64 24", out fetchAddr);

        if (!found)
        {
            Plugin.Log.Warning("[Sentinel][Net] FetchReceivedPacket signature not found. " +
                               "NetworkCastListener using ObjectTable fallback only.");
            return;
        }

        _fetchHook = interop.HookFromAddress<FetchReceivedPacketDelegate>(fetchAddr, FetchReceivedPacketDetour);
        _fetchHook.Enable();
        Plugin.Log.Information("[Sentinel][Net] Initialized via FetchReceivedPacket hook at 0x{Addr:X}. " +
                               "Approach: raw IPC packet interception.", fetchAddr.ToInt64());
    }

    public void Dispose()
    {
        _fetchHook?.Disable();
        _fetchHook?.Dispose();
    }

    // ── FetchReceivedPacket detour ────────────────────────────────────────

    private bool FetchReceivedPacketDetour(void* self, RawReceivedPacket* outData)
    {
        var result = _fetchHook!.Original(self, outData);
        if (!result || outData->IPC == null) return result;

        var ipc = outData->IPC;
        if (ipc->PacketData == null) return result;

        var opcode = ipc->PacketData->MessageType;
        var payloadPtr = (byte*)(ipc->PacketData) + sizeof(RawIPCHeader); // payload starts after header
        var sourceActor = (ulong)ipc->SourceActor;

        if (opcode == _opcodeActorCast)
        {
            HandleActorCast(sourceActor, payloadPtr);
        }
        else if (opcode == _opcodeActorControl)
        {
            HandleActorControl(sourceActor, payloadPtr);
        }
        else if (opcode == _opcodeActionEffect1  ||
                 opcode == _opcodeActionEffect8  ||
                 opcode == _opcodeActionEffect16 ||
                 opcode == _opcodeActionEffect24 ||
                 opcode == _opcodeActionEffect32)
        {
            HandleActionEffect(sourceActor, payloadPtr);
        }

        return result;
    }

    // ── Packet handlers ───────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HandleActorCast(ulong sourceActor, byte* payload)
    {
        // SpellID at +0x00 (ushort) — stable, no scramble needed
        var spellId   = *(ushort*)(payload + 0x00);
        var castTime  = *(float*) (payload + 0x08);
        var rawRot    = *(ushort*)(payload + 0x10);
        var rawPosX   = *(ushort*)(payload + 0x18);
        var rawPosY   = *(ushort*)(payload + 0x1A);
        var rawPosZ   = *(ushort*)(payload + 0x1C);

        // Decode rotation: (raw / 65535) * 2π - π
        float rotation = (rawRot / 65535f) * (2f * MathF.PI) - MathF.PI;

        // Decode position: (raw / 65535) * 2000 - 1000
        const float PosFactor = 2000f / 65535f;
        float x = rawPosX * PosFactor - 1000f;
        float y = rawPosY * PosFactor - 1000f;
        float z = rawPosZ * PosFactor - 1000f;

        CastStarts.Enqueue(new CastStartEvent(
            sourceActor, spellId, castTime, rotation, x, y, z));
        TotalCastStarts++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HandleActorControl(ulong sourceActor, byte* payload)
    {
        var category = *(ushort*)(payload + 0x00);
        if (category != CancelCastCategory) return;

        // param3 at +0x0C holds the ActionID (scrambled), but we use param2 (ActionType)
        // and param3 together. For cancellation matching, we only need the source entity.
        // param3 is scrambled; we store 0 as the actionId since we match by entityId only.
        var actionIdScrambled = *(uint*)(payload + 0x0C);

        // Note: param3 is scrambled. For CastCancel matching in CastScanner we
        // only need the sourceActor (entityId) to find and remove the active cast.
        // We store the scrambled value but CastScanner ignores it for lookups.
        CastCancels.Enqueue(new CastCancelEvent(sourceActor, actionIdScrambled));
        TotalCastCancels++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HandleActionEffect(ulong sourceActor, byte* payload)
    {
        // ActionEffectHeader layout (payload starts here):
        //   +0x00 ulong  animationTargetId
        //   +0x08 uint   actionId (scrambled)
        //   ...
        //   +0x1C ushort actionAnimationId  — same as SpellID, not scrambled
        var actionAnimId = *(ushort*)(payload + 0x1C);
        ActionResolves.Enqueue(new ActionResolveEvent(sourceActor, actionAnimId));
        TotalActionResolves++;
    }

    // ── Opcode discovery ──────────────────────────────────────────────────

    private readonly struct KnownOpcodes
    {
        public readonly ushort ActorCast;
        public readonly ushort ActorControl;
        public readonly ushort ActionEffect1;
        public readonly ushort ActionEffect8;
        public readonly ushort ActionEffect16;
        public readonly ushort ActionEffect24;
        public readonly ushort ActionEffect32;

        public KnownOpcodes(
            ushort actorCast, ushort actorControl,
            ushort ae1, ushort ae8, ushort ae16, ushort ae24, ushort ae32)
        {
            ActorCast      = actorCast;
            ActorControl   = actorControl;
            ActionEffect1  = ae1;
            ActionEffect8  = ae8;
            ActionEffect16 = ae16;
            ActionEffect24 = ae24;
            ActionEffect32 = ae32;
        }
    }

    // Stable PacketID indices (from BMR's ServerIPC.PacketID enum):
    private const int PktIdActorCast      = 280;
    private const int PktIdActorControl   = 226;
    private const int PktIdActionEffect1  = 230;
    private const int PktIdActionEffect8  = 233;
    private const int PktIdActionEffect16 = 234;
    private const int PktIdActionEffect24 = 235;
    private const int PktIdActionEffect32 = 236;

    /// <summary>
    /// Discovers live wire opcodes by scanning the game's IPC dispatch function.
    /// Uses the same technique as BMR's OpcodeMap.
    /// </summary>
    private static unsafe KnownOpcodes DiscoverOpcodes(ISigScanner sigScanner)
    {
        // Scan for the IPC dispatch function.
        // It starts with:
        //   mov rax, [r8+10h]
        //   mov r10, [rax+38h]
        //   movzx eax, word ptr [r10+2]
        //   add eax, -<min_case>
        //   cmp eax, <max_case-min_case>
        //   ja <default_off>
        //   lea r11, <__ImageBase_off>
        //   cdqe
        //   mov r9d, ds::<jumptable_rva>[r11+rax*4]
        var func = (byte*)sigScanner.ScanText(
            "49 8B 40 10  4C 8B 50 38  41 0F B7 42 02  83 C0 ??  3D ?? ?? ?? ??  " +
            "0F 87 ?? ?? ?? ??  4C 8D 1D ?? ?? ?? ??  48 98  45 8B 8C 83 ?? ?? ?? ??");

        // Read jump table parameters
        var minCase       = -*(sbyte*)(func + 15);
        var jumptableSize = *(int*)(func + 17) + 1;
        var defaultAddr   = ReadRVA(func + 23);
        var imageBase     = ReadRVA(func + 30);
        var jumptable     = (int*)(imageBase + *(int*)(func + 40));

        // vtable-index → wire opcode mapping
        var idToOpcode = new System.Collections.Generic.Dictionary<int, int>();

        // Expected body pattern for each case: 9 bytes prefix then jmp [rax+offset]
        ReadOnlySpan<byte> bodyPrefix = [0x48, 0x8B, 0x01, 0x4D, 0x8D, 0x4A, 0x10, 0x48, 0xFF];

        for (int i = 0; i < jumptableSize; i++)
        {
            var bodyAddr = imageBase + jumptable[i];
            if (bodyAddr == defaultAddr) continue;

            var opcode = minCase + i;
            var index  = ReadVtableIndex(bodyAddr, bodyPrefix);
            if (index < 0) continue;

            if (!idToOpcode.ContainsKey(index))
                idToOpcode[index] = opcode;
        }

        static ushort Get(System.Collections.Generic.Dictionary<int, int> map, int id, string name)
        {
            if (!map.TryGetValue(id, out var v))
                throw new InvalidOperationException($"[Sentinel][Net] Could not resolve opcode for {name} (index {id})");
            return (ushort)v;
        }

        return new KnownOpcodes(
            actorCast:      Get(idToOpcode, PktIdActorCast,      "ActorCast"),
            actorControl:   Get(idToOpcode, PktIdActorControl,   "ActorControl"),
            ae1:            Get(idToOpcode, PktIdActionEffect1,  "ActionEffect1"),
            ae8:            Get(idToOpcode, PktIdActionEffect8,  "ActionEffect8"),
            ae16:           Get(idToOpcode, PktIdActionEffect16, "ActionEffect16"),
            ae24:           Get(idToOpcode, PktIdActionEffect24, "ActionEffect24"),
            ae32:           Get(idToOpcode, PktIdActionEffect32, "ActionEffect32")
        );
    }

    private static unsafe byte* ReadRVA(byte* p) => p + 4 + *(int*)p;

    private static unsafe int ReadVtableIndex(byte* bodyAddr, ReadOnlySpan<byte> prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
            if (bodyAddr[i] != prefix[i]) return -1;

        int len = prefix.Length;
        int vtoff = bodyAddr[len] switch
        {
            0x60 => *(bodyAddr + len + 1),
            0xA0 => *(int*)(bodyAddr + len + 1),
            _    => -1
        };
        if (vtoff < 0x10 || (vtoff & 7) != 0) return -1;
        return (vtoff >> 3) - 2;
    }
}
