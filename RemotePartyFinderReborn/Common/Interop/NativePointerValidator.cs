using System;

namespace RemotePartyFinderReborn;

internal static unsafe class NativePointerValidator {
    private const nuint MinUserAddress = 0x10000;
    private const ulong MaxUserAddress64 = 0x00007FFFFFFFFFFFUL;
    private const uint MaxUserAddress32 = 0x7FFFFFFFU;

    internal static bool IsValidUserPointer(void* pointer) {
        if (pointer == null) {
            return false;
        }

        var address = (nuint)pointer;
        if (address < MinUserAddress) {
            return false;
        }

        return IntPtr.Size == 8
            ? (ulong)address <= MaxUserAddress64
            : (ulong)address <= MaxUserAddress32;
    }

    internal static bool IsValidUserPointer(nint pointer) {
        return IsValidUserPointer((void*)pointer);
    }
}
