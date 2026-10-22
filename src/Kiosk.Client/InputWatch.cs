using System.Runtime.InteropServices;

namespace Kiosk.Client;

/// <summary>
/// The time of the last real key or mouse/touch input, seen by low-level hooks on a thread of their own (so a busy
/// screen never delays them). Used while a message film plays: the film keeps the display awake, and Windows then
/// also moves its own "last input" time, so the lock and the screensaver would never start.
/// The hooks miss input aimed at a window of a higher level (the card sign-in screen runs as SYSTEM) and Windows may
/// drop them silently, so the mouse pointer's position and the keyboard's keys are also watched, and the hooks are
/// renewed every minute.
/// </summary>
internal static class InputWatch
{
    private const int KeyboardHook = 13, MouseHook = 14, WmTimer = 0x0113, PollMilliseconds = 150;
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private static readonly HookProc Proc = OnInput;   // Kept alive for as long as the hooks are.
    private static long lastInput = Environment.TickCount64;
    private static int started;

    internal static bool Running { get; private set; }
    internal static uint IdleMilliseconds => (uint)Math.Min(uint.MaxValue, Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref lastInput)));

    /// <summary>Starts once (the first film); the hooks then stay until the Kiosk closes.</summary>
    internal static void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        var thread = new Thread(() =>
        {
            var module = GetModuleHandle(null);
            var keyboard = SetWindowsHookEx(KeyboardHook, Proc, module, 0);
            var mouse = SetWindowsHookEx(MouseHook, Proc, module, 0);
            SetTimer(IntPtr.Zero, IntPtr.Zero, PollMilliseconds, IntPtr.Zero);   // WM_TIMER on this thread's queue.
            if (keyboard == IntPtr.Zero || mouse == IntPtr.Zero)
            {
                Audit.Write("input_watch_failed", Marshal.GetLastWin32Error().ToString());
                if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard);
                if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
                return;
            }
            Interlocked.Exchange(ref lastInput, Environment.TickCount64);
            Running = true;
            GetCursorPos(out var pointer);
            int ticks = 0;
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Window == IntPtr.Zero && message.Id == WmTimer)
                {
                    // The pointer moved: real input, whichever window it went to.
                    if (GetCursorPos(out var now) && (now.X != pointer.X || now.Y != pointer.Y)) { pointer = now; Interlocked.Exchange(ref lastInput, Environment.TickCount64); }
                    // A key held or pressed since the last look: typing counts also when the hook did not see it (PIN screen).
                    else if (AnyKey()) Interlocked.Exchange(ref lastInput, Environment.TickCount64);
                    if (++ticks % (60_000 / PollMilliseconds) == 0)
                    {
                        // Windows removes a hook it judged too slow without saying so: put them in again.
                        UnhookWindowsHookEx(keyboard); UnhookWindowsHookEx(mouse);
                        keyboard = SetWindowsHookEx(KeyboardHook, Proc, module, 0);
                        mouse = SetWindowsHookEx(MouseHook, Proc, module, 0);
                    }
                    continue;
                }
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
            Running = false;
        }) { IsBackground = true, Name = "Kiosk input watch" };
        thread.Start();
    }

    private static bool AnyKey()
    {
        bool pressed = false;
        // Every key is asked (no early exit): the "pressed since the last call" bit is then cleared for all of them.
        for (int key = 0x08; key <= 0xFE; key++) if ((GetAsyncKeyState(key) & 0x8001) != 0) pressed = true;
        return pressed;
    }

    private static long reportedAt = -600_000;

    /// <summary>Audits (at most every 10 minutes) when Windows' input clock was moved without real input during a film.</summary>
    internal static void Compare(uint windowsIdle)
    {
        uint real = IdleMilliseconds;
        if (real < windowsIdle + 60_000 || Environment.TickCount64 - reportedAt < 600_000) return;
        reportedAt = Environment.TickCount64;
        Audit.Write("idle_kept_by_film", "Windows " + windowsIdle / 1000 + " s, real input " + real / 1000 + " s");
    }

    private static IntPtr OnInput(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0) Interlocked.Exchange(ref lastInput, Environment.TickCount64);
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public IntPtr Window; public uint Id; public IntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr window, IntPtr id, uint milliseconds, IntPtr callback);
}
