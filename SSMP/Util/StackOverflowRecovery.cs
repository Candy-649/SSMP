using System;
using System.Runtime.InteropServices;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Util;

/// <summary>
/// Lets the game live through a stack overflow in C# code, which otherwise closes it without a word in any log.
///
/// Mono turns a stack overflow on Windows into a StackOverflowException: its handler unwinds the stack to the catch
/// and resumes in a short piece of code it generated at startup, which puts the guard page of the stack back
/// (<c>_resetstkoflw</c>) and then jumps into the catch. That piece of code starts on the stack pointer of the catch,
/// which is aligned to 16 bytes, pushes one register and calls on, so everything under it runs with the stack 8 bytes
/// off the alignment that Windows requires. Putting the guard page back loads a system library the first time, and the
/// loader saves registers with instructions that fault on such a stack: the game dies with an access violation in
/// ntdll.dll (offset 0x3ed12), inside <c>RtlDosApplyFileIsolationRedirection_Ustr</c>, and the exception is never
/// thrown. Rewriting the first eight bytes of that code to align the stack itself lets the exception reach its catch,
/// where it is logged with the calls that ran out of stack.
/// </summary>
internal static class StackOverflowRecovery {
    /// <summary>
    /// The Mono runtime of the game.
    /// </summary>
    private const string MonoModuleName = "mono-2.0-bdwgc.dll";

    /// <summary>
    /// Where the Mono runtime of the game keeps the address of that code (<c>restore_stack</c> in exceptions-amd64.c),
    /// from the start of its module. Found in three crash dumps of this build; another build is recognised by the code
    /// not being there, and is left alone.
    /// </summary>
    private const int RestoreStackRva = 0x745328;

    /// <summary>
    /// The code as Mono generates it, up to the address of the first call: push rbp; mov rbp, rsp; sub rsp, 32;
    /// mov r11, imm64.
    /// </summary>
    private static readonly byte[] Generated = [0x55, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x20, 0x49, 0xBB];

    /// <summary>
    /// The first eight bytes rewritten: and rsp, -16; sub rsp, 32. Nothing after them reads the frame pointer that
    /// the original pushed and set, and the code never returns.
    /// </summary>
    private static readonly byte[] Aligned = [0x48, 0x83, 0xE4, 0xF0, 0x48, 0x83, 0xEC, 0x20];

    private const uint MemCommit = 0x1000;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;

    /// <summary>
    /// Rewrites the code that Mono resumes in after a stack overflow, if this is the build of Mono it was found in.
    /// </summary>
    public static void Apply() {
        var mono = GetModuleHandleW(MonoModuleName);
        if (mono == IntPtr.Zero) {
            Logger.Info($"Stack overflow recovery: {MonoModuleName} is not loaded, nothing changed");
            return;
        }

        var slot = mono + RestoreStackRva;
        if (!Query(slot, out var slotInfo) || slotInfo.AllocationBase != mono) {
            Logger.Info("Stack overflow recovery: this build of Mono has no such place, nothing changed");
            return;
        }

        var code = Marshal.ReadIntPtr(slot);
        if (code == IntPtr.Zero || !Query(code, out var codeInfo) ||
            codeInfo.Protect != PageExecuteRead && codeInfo.Protect != PageExecuteReadWrite ||
            codeInfo.BaseAddress.ToInt64() + codeInfo.RegionSize.ToInt64() - code.ToInt64() < Generated.Length) {
            Logger.Info($"Stack overflow recovery: no code at {code.ToInt64():x} in this Mono, nothing changed");
            return;
        }

        var found = new byte[Generated.Length];
        Marshal.Copy(code, found, 0, found.Length);
        if (!found.AsSpan().SequenceEqual(Generated)) {
            Logger.Info(
                $"Stack overflow recovery: the code at {code.ToInt64():x} is not what Mono generates " +
                $"({BitConverter.ToString(found)}), nothing changed"
            );
            return;
        }

        if (!VirtualProtect(code, (UIntPtr) Aligned.Length, PageExecuteReadWrite, out var oldProtect)) {
            Logger.Warn($"Stack overflow recovery: the code could not be made writable ({Marshal.GetLastWin32Error()})");
            return;
        }

        Marshal.Copy(Aligned, 0, code, Aligned.Length);
        VirtualProtect(code, (UIntPtr) Aligned.Length, oldProtect, out _);
        FlushInstructionCache(GetCurrentProcess(), code, (UIntPtr) Aligned.Length);

        Logger.Info(
            $"Stack overflow recovery: aligned the stack in the code Mono resumes in after a stack overflow, at " +
            $"{code.ToInt64():x}, so that one is logged as a StackOverflowException instead of closing the game"
        );
    }

    /// <summary>
    /// Whether the memory at an address is committed, and what the region it lies in is.
    /// </summary>
    private static bool Query(IntPtr address, out MemoryBasicInformation info) {
        return VirtualQuery(address, out info, (UIntPtr) Marshal.SizeOf<MemoryBasicInformation>()) != UIntPtr.Zero &&
               info.State == MemCommit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
