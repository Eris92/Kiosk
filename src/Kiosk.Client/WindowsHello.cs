using System.Runtime.InteropServices;

namespace Kiosk.Client;

/// <summary>
/// Windows Hello confirmation (Windows.Security.Credentials.UI.UserConsentVerifier), called straight through the
/// WinRT ABI: Kiosk then needs no Windows SDK projection (Microsoft.Windows.SDK.NET.dll, 24 MB of the package).
/// </summary>
internal static class WindowsHello
{
    internal enum Result { Verified, DeviceNotPresent, NotConfiguredForUser, DisabledByPolicy, DeviceBusy, RetriesExhausted, Canceled }

    private const string VerifierClass = "Windows.Security.Credentials.UI.UserConsentVerifier";
    private static readonly Guid InteropIid = new("39E050C3-4E74-441A-8DC0-B81104DF949C");      // IUserConsentVerifierInterop
    private static readonly Guid AsyncInfoIid = new("00000036-0000-0000-C000-000000000046");    // IAsyncInfo
    // IAsyncOperation<UserConsentVerificationResult>: WinRT's IID for that parameterized interface (SHA-1 of its signature).
    private static readonly Guid OperationIid = new("FD596FFD-2318-558F-9DBE-D21DF43764A5");

    /// <summary>Shows Windows Hello over <paramref name="window"/> and waits for the person's answer.</summary>
    internal static async Task<Result> VerifyAsync(IntPtr window, string message)
    {
        var operation = Start(window, message);
        try
        {
            var info = QueryInterface(operation, AsyncInfoIid);
            try
            {
                int status;
                while ((status = Call(info, 7)) == 0) await Task.Delay(100);   // IAsyncInfo.Status: 0 started, 1 completed, 2 canceled, 3 error.
                if (status == 2) return Result.Canceled;
                if (status != 1) throw new COMException("Windows Hello nie odpowiedziało.", Call(info, 8));   // IAsyncInfo.ErrorCode
            }
            finally { Marshal.Release(info); }
            return (Result)Call(operation, 8);   // IAsyncOperation.GetResults
        }
        finally { Marshal.Release(operation); }
    }

    private static unsafe IntPtr Start(IntPtr window, string message)
    {
        Check(WindowsCreateString(VerifierClass, VerifierClass.Length, out var classId));
        IntPtr factory;
        try { var iid = InteropIid; Check(RoGetActivationFactory(classId, ref iid, out factory)); }
        finally { WindowsDeleteString(classId); }
        var text = IntPtr.Zero;
        try
        {
            Check(WindowsCreateString(message, message.Length, out text));
            var iid = OperationIid;
            IntPtr operation;
            // IInspectable's six methods, then RequestVerificationForWindowAsync(HWND, HSTRING, REFIID, void**).
            var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[6];
            Check(method(factory, window, text, &iid, &operation));
            return operation;
        }
        finally
        {
            if (text != IntPtr.Zero) WindowsDeleteString(text);
            Marshal.Release(factory);
        }
    }

    /// <summary>A method that only returns one 32-bit value: get_Status, get_ErrorCode, GetResults (enum).</summary>
    private static unsafe int Call(IntPtr instance, int slot)
    {
        int value;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)(*(IntPtr**)instance)[slot])(instance, &value));
        return value;
    }

    private static unsafe IntPtr QueryInterface(IntPtr instance, Guid iid)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)instance)[0])(instance, &iid, &result));
        return result;
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr text);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr text);
}
