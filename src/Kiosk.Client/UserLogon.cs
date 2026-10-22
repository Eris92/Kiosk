using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiosk.Client;

/// <summary>
/// "Zaloguj" as the person's own Windows / Entra ID account, like runas: Windows' credential dialog asks for
/// the login and password, Windows checks them, and Kiosk then starts that person's programs (Edge, apps) as
/// them, with their profile, bookmarks and SSO. The password is kept only in this process's memory, encrypted
/// with CryptProtectMemory, until the Kiosk session is closed; it is never written anywhere.
/// </summary>
internal sealed class UserLogon : IDisposable, IProgramStarter
{
    private readonly string user;
    private readonly string? domain;
    private byte[]? secret; // UTF-16 password, CryptProtectMemory(SAME_PROCESS), padded to 16 bytes.
    private readonly int length;
    /// <summary>Signed in with the card alone (no password here): programs start through the sign-in program.</summary>
    private FidoSignIn? card;
    internal bool WithCard => card != null;

    /// <summary>The account as Windows shows it, e.g. AzureAD\JanKowalski.</summary>
    internal string DisplayName { get; }
    /// <summary>The login typed in the dialog (UPN or DOMAIN\user), for RDP.</summary>
    internal string Login => domain == null ? user : domain + "\\" + user;

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

    /// <summary>Card + PIN: the Windows sign-in with the card itself; the person's token stays in the sign-in program.</summary>
    internal static UserLogon FromCard(FidoSignIn signIn)
    {
        // "AzureAD\JanKowalski" from Windows; the card names the account (jan@firma.pl), which remote desktops use.
        var name = signIn.WindowsName;
        int slash = name.IndexOf('\\');
        string? domain = slash > 0 ? name[..slash] : null;
        var account = signIn.Account.Contains('@') ? signIn.Account : slash > 0 ? name[(slash + 1)..] : name;
        var logon = new UserLogon(account, domain, name)
        {
            card = signIn, Sid = signIn.Sid, IsAdministrator = signIn.IsAdministrator,
            FriendlyName = People.FriendlyName("", account, name)
        };
        return logon;
    }

    private UserLogon(string user, string? domain, string displayName)
    {
        this.user = user;
        this.domain = domain;
        DisplayName = displayName;
    }

    internal string User => user;
    internal string Sid { get; private set; } = "";
    /// <summary>The person's name for the desktop, e.g. "Jan Kowalski" (not FIRMA\jkowalski).</summary>
    internal string FriendlyName { get; private set; } = "";
    /// <summary>The person's Windows profile folder (from ProfileList), or null before their first sign-in on this PC.</summary>
    internal string? ProfileDirectory =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + Sid, "ProfileImagePath", null) as string;
    internal string? Domain => domain;
    /// <summary>The account is in this PC's local Administrators group (also through a group, also UAC-filtered).</summary>
    internal bool IsAdministrator { get; private set; }

    /// <summary>Starts a program as this person (with their profile), suspended, like runas.</summary>
    public (IntPtr Process, IntPtr Thread) Start(string application, string commandLine, string? directory, short show = 4)
    {
        if (card != null) return card.Start(application, commandLine, directory, show);
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

    private Microsoft.Win32.SafeHandles.SafeAccessTokenHandle? token;

    /// <summary>Runs <paramref name="work"/> as this person (Pliki: their folders, with their rights).</summary>
    internal object? RunAs(Func<object?> work)
    {
        if (card != null) throw new NotSupportedException("Pliki osoby zalogowanej kartą otwierają się w osobnym oknie.");
        if (token == null || token.IsInvalid)
        {
            var password = Reveal();
            try { if (!LogonUser(user, domain, password, 2, 0, out token)) throw new Win32Exception(); }   // LOGON32_LOGON_INTERACTIVE
            finally { Array.Clear(password); }
        }
        return System.Security.Principal.WindowsIdentity.RunImpersonated(token, work);
    }

    /// <summary>
    /// First use on this PC: Windows creates the person's profile (C:\Users\…) when a program first starts as them.
    /// Do it once with a hidden "cmd /c exit" and wait, so Edge and the apps start in a finished profile instead of
    /// racing its creation. Returns false when Windows still has no profile for them.
    /// </summary>
    internal bool EnsureProfile()
    {
        if (ProfileDirectory != null) return true;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var (process, thread) = Start(cmd, "\"" + cmd + "\" /c exit", Environment.SystemDirectory, show: 0);
        try
        {
            ResumeThread(thread);
            WaitForSingleObject(process, 120000);
        }
        finally { CloseHandle(thread); CloseHandle(process); }
        Audit.Write(ProfileDirectory != null ? "profile_created" : "profile_missing", DisplayName);
        return ProfileDirectory != null;
    }

    /// <summary>The password for the RDP control; the caller clears it as soon as it is handed over.</summary>
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

    public void Dispose()
    {
        if (secret != null) { Array.Clear(secret); secret = null; }
        token?.Dispose(); token = null;
        // Signs the person out: their programs close with it. In the background: the answer can take seconds.
        if (card is { } signIn) { card = null; _ = Task.Run(signIn.Dispose); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    /// <summary>The name in Active Directory / Entra ID ("Jan Kowalski"), asked as that person; empty when unknown.</summary>
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
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string? domain, char[] password, int type, int provider, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithLogonW(string user, string? domain, char[] password, int logonFlags, string application,
        StringBuilder commandLine, int creationFlags, IntPtr environment, string? directory, ref AppNative.StartupInfo startup, out ProcessInformation process);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectMemory(byte[] data, int size, int flags);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectMemory(byte[] data, int size, int flags);
}
