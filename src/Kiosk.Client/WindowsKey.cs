using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kiosk.Client;

/// <summary>
/// The Windows key opens and closes the Kiosk's Start menu. A low-level keyboard hook sees the key whatever has the
/// keyboard (browser, hosted program) and keeps it from Windows, so Explorer's own Start menu does not open as well.
/// Win + another key still works: the Win key is handed to Windows just before the other key.
/// </summary>
internal sealed class WindowsKey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint LLKHF_INJECTED = 0x10;

    private readonly HookProc proc;   // kept alive for the native callback
    private readonly IntPtr hook;
    private readonly Action pressed;
    private int held;                 // the Win key being held (0: none)
    private bool combined;

    internal WindowsKey(Action pressed)
    {
        this.pressed = pressed;
        proc = Callback;
        using var current = Process.GetCurrentProcess();
        using var module = current.MainModule;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module?.ModuleName), 0);
        if (hook == IntPtr.Zero) Audit.Write("windows_key_unavailable", "Win32 " + Marshal.GetLastWin32Error());
    }

    private IntPtr Callback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KeyInfo>(data);
            int msg = message.ToInt32();
            bool keyDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN, keyUp = msg is WM_KEYUP or WM_SYSKEYUP;
            bool injected = (key.Flags & LLKHF_INJECTED) != 0;
            if (!injected && key.Key is VK_LWIN or VK_RWIN)
            {
                if (keyDown)
                {
                    if (held == 0) { held = key.Key; combined = false; }
                    if (!combined) return 1; // Held back until we know whether it is pressed alone.
                }
                else if (keyUp && held != 0)
                {
                    held = 0;
                    if (!combined) { pressed(); return 1; }
                    // A combination: this key-up releases the Win key that was handed to Windows.
                }
            }
            else if (!injected && keyDown && held != 0 && !combined)
            {
                combined = true;
                var win = new[] { new Input { Type = 1, Keyboard = new KeyboardInput { Key = (ushort)held } } };
                SendInput(1, win, Marshal.SizeOf<Input>());
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    public void Dispose() { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyInfo { public int Key, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }
    // INPUT is a union sized for MOUSEINPUT (the largest member): 40 bytes on x64.
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KeyboardInput Keyboard; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
