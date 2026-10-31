using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace Kiosk.Client;
internal sealed partial class UserLogon
{
    private readonly string user = "";
    private readonly string? domain;
    private byte[]? secret;
    private readonly int length;
    internal string User => user;
    internal string? Domain => domain;
    private UserLogon(string user, string? domain, char[] password, int length, string displayName)
    {
        this.user = user;
        this.domain = domain;
        DisplayName = displayName;
        this.length = length;
        secret = new byte[(length * 2 + 15) / 16 * 16];
        Encoding.Unicode.GetBytes(password, 0, length, secret, 0);
        if (!CryptProtectMemory(secret, secret.Length, 0)) throw new Win32Exception();
    }

    private const int SmartCardLogonRequired = unchecked((int)0x8009033E);   // SEC_E_SMARTCARD_LOGON_REQUIRED

    /// <summary>Why a saved or typed password did not sign in, in words the person can act on.</summary>
    internal static string Explain(int code) => code == SmartCardLogonRequired
        ? "Windows na tym komputerze nie przyjmuje logowania hasłem (zasada „Wymagaj Windows Hello dla firm lub karty inteligentnej”, scforceoption). " +
          "„Zaloguj” loginem i hasłem nie zadziała, dopóki administrator jej nie wyłączy; zaloguj się do Windows kartą (tryb Powłoka Windows)."
        : "Hasło konta zmieniło się (kod " + code + "). Podaj nowe.";

    internal static UserLogon? TryLogon(string login, string? domain, char[] password, int length, out int code)
    {
        bool ok = LogonUser(login, domain, password, 2, 0, out var token); // LOGON32_LOGON_INTERACTIVE
        code = ok ? 0 : Marshal.GetLastWin32Error();
        // Entra ID accounts are "AzureAD\jan@firma.pl" on this PC (as in runas /user:AzureAD\...).
        if (!ok && domain == null && login.Contains('@'))
        {
            token.Dispose();
            ok = LogonUser(login, "AzureAD", password, 2, 0, out token);
            if (ok) domain = "AzureAD";
            else code = Marshal.GetLastWin32Error();
        }
        using (token)
        {
            if (!ok) { Audit.Write("signin_failed", "error " + code); return null; } // Never the login or password.
            using var identity = new System.Security.Principal.WindowsIdentity(token.DangerousGetHandle());
            return new UserLogon(login, domain, password, length, identity.Name)
            {
                Sid = identity.User?.Value ?? "", FriendlyName = People.FriendlyName(DirectoryName(identity), login, identity.Name),
                IsAdministrator = identity.Groups?.Any(g => g.Value == "S-1-5-32-544") == true
            };
        }
    }

    /// <summary>Shows the Windows credential dialog and checks the account; null when cancelled.</summary>
    internal static UserLogon? Prompt(IntPtr owner, string message)
    {
        var info = new CredUiInfo { Size = Marshal.SizeOf<CredUiInfo>(), Parent = owner, Caption = "Zaloguj do Kiosku", Message = message };
        uint package = 0;
        bool save = false;
        string? error = null;
        while (true)
        {
            if (error != null) info.Message = error + "\n" + message;
            int result = CredUIPromptForWindowsCredentials(ref info, error == null ? 0 : 1326, ref package, IntPtr.Zero, 0,
                out var buffer, out var size, ref save, 0x1); // CREDUIWIN_GENERIC: login + password
            if (result == 1223) return null; // ERROR_CANCELLED
            if (result != 0) throw new Win32Exception(result);
            var name = new StringBuilder(514);
            var dom = new StringBuilder(338);
            var password = new char[257];
            int nameLength = name.Capacity, domLength = dom.Capacity, passwordLength = password.Length;
            try
            {
                if (!CredUnPackAuthenticationBuffer(1, buffer, size, name, ref nameLength, dom, ref domLength, password, ref passwordLength))
                    throw new Win32Exception(); // 1 = CRED_PACK_PROTECTED_CREDENTIALS: a usable (unprotected) password
                var login = name.ToString();
                string? domainPart = dom.Length > 0 ? dom.ToString() : null;
                int slash = login.IndexOf('\\');
                if (domainPart == null && slash > 0) { domainPart = login[..slash]; login = login[(slash + 1)..]; }
                int typedLength = Array.IndexOf(password, '\0');
                if (typedLength < 0) typedLength = Math.Min(passwordLength, password.Length - 1);
                var typed = new char[typedLength + 1]; // NUL-terminated for LogonUser
                Array.Copy(password, typed, typedLength);
                try
                {
                    var logon = TryLogon(login, domainPart, typed, typedLength, out int code);
                    if (logon != null) return logon;
                    error = code == SmartCardLogonRequired ? Explain(code)
                        : "Nieprawidłowy login lub hasło (" + new Win32Exception(code).Message + ", kod " + code + ").";
                    continue;
                }
                finally { Array.Clear(typed); }
            }
            finally
            {
                Array.Clear(password);
                RtlZeroMemory(buffer, size);
                CoTaskMemFree(buffer);
            }
        }
    }

    public (IntPtr Process, IntPtr Thread) StartLocal(string application, string commandLine, string? directory, short show = 4)
    {
        var startup = new AppNative.StartupInfo { Size = Marshal.SizeOf<AppNative.StartupInfo>(), Flags = 1, ShowWindow = show };
        var password = Reveal();
        try
        {
            // LOGON_WITH_PROFILE; CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT
            if (!CreateProcessWithLogonW(user, domain, password, 1, application, new StringBuilder(commandLine), 0x4 | 0x400,
                    IntPtr.Zero, directory, ref startup, out var process))
                throw new Win32Exception();
            return (process.Process, process.Thread);
        }
        finally { Array.Clear(password); }
    }

    internal char[] Reveal()
    {
        if (card != null) throw new InvalidOperationException("Logowanie kartą nie używa hasła.");
        if (secret == null) throw new ObjectDisposedException(nameof(UserLogon));
        var copy = (byte[])secret.Clone();
        try
        {
            if (!CryptUnprotectMemory(copy, copy.Length, 0)) throw new Win32Exception();
            var password = new char[length + 1]; // NUL-terminated for the native calls
            Encoding.Unicode.GetChars(copy, 0, length * 2, password, 0);
            return password;
        }
        finally { Array.Clear(copy); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo { public int Size; public IntPtr Parent; public string Message; public string Caption; public IntPtr Banner; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    private static string DirectoryName(System.Security.Principal.WindowsIdentity identity)
    {
        try
        {
            return System.Security.Principal.WindowsIdentity.RunImpersonated(identity.AccessToken, () =>
            {
                var buffer = new StringBuilder(256);
                uint size = (uint)buffer.Capacity;
                return GetUserNameEx(3, buffer, ref size) ? buffer.ToString() : ""; // NameDisplay
            });
        }
        catch (Exception) { return ""; }
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);
    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentials(ref CredUiInfo info, int error, ref uint package, IntPtr inBuffer, uint inSize,
        out IntPtr outBuffer, out uint outSize, ref bool save, int flags);
    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBuffer(int flags, IntPtr buffer, uint size, StringBuilder user, ref int userLength,
        StringBuilder domain, ref int domainLength, [Out] char[] password, ref int passwordLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string? domain, char[] password, int type, int provider, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithLogonW(string user, string? domain, char[] password, int logonFlags, string application,
        StringBuilder commandLine, int creationFlags, IntPtr environment, string? directory, ref AppNative.StartupInfo startup, out ProcessInformation process);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectMemory(byte[] data, int size, int flags);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectMemory(byte[] data, int size, int flags);
    [DllImport("kernel32.dll")] private static extern void RtlZeroMemory(IntPtr destination, uint length);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr pointer);
}
