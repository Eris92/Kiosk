using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kiosk.Client;

/// <summary>
/// The person signed in to Windows with a card and PIN. The sign-in broker keeps their token;
/// the Kiosk asks it to start programs as that person without receiving a password or token.
/// </summary>
internal sealed partial class UserLogon : IDisposable, IProgramStarter
{
    private FidoSignIn? card;

    /// <summary>The account as Windows shows it, e.g. AzureAD\JanKowalski.</summary>
    internal string DisplayName { get; }

    /// <summary>Card + PIN: the Windows sign-in with the card itself; the person's token stays in the sign-in program.</summary>
    internal static UserLogon FromCard(FidoSignIn signIn)
    {
        // "AzureAD\JanKowalski" from Windows; the card names the account (jan@firma.pl), which remote desktops use.
        var name = signIn.WindowsName;
        int slash = name.IndexOf('\\');
        var account = signIn.Account.Contains('@') ? signIn.Account : slash > 0 ? name[(slash + 1)..] : name;
        var logon = new UserLogon(name)
        {
            card = signIn, Sid = signIn.Sid, IsAdministrator = signIn.IsAdministrator,
            FriendlyName = People.FriendlyName(signIn.PersonName, account, name)
        };
        return logon;
    }

    private UserLogon(string displayName) => DisplayName = displayName;

    internal string Sid { get; private set; } = "";
    /// <summary>The person's name for the desktop, e.g. "Jan Kowalski" (not FIRMA\jkowalski).</summary>
    internal string FriendlyName { get; private set; } = "";
    /// <summary>The person's Windows profile folder (from ProfileList), or null before their first sign-in on this PC.</summary>
    internal string? ProfileDirectory =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + Sid, "ProfileImagePath", null) as string;
    /// <summary>The account is in this PC's local Administrators group (also through a group, also UAC-filtered).</summary>
    internal bool IsAdministrator { get; private set; }

    /// <summary>Starts a program as this person, suspended, through the sign-in broker.</summary>
    public (IntPtr Process, IntPtr Thread) Start(string application, string commandLine, string? directory, short show = 4)
        => card?.Start(application, commandLine, directory, show) ?? StartLocal(application, commandLine, directory, show);

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
            if (ResumeThread(thread) == uint.MaxValue)
            {
                var error = new Win32Exception(Marshal.GetLastPInvokeError());
                TerminateProcess(process, 1);
                throw error;
            }
            var waited = WaitForSingleObject(process, 120000);
            if (waited != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                TerminateProcess(process, 1);
                if (waited == 0x102) throw new TimeoutException("Tworzenie profilu Windows przekroczyło 120 sekund.");
                throw new Win32Exception(error);
            }
        }
        finally { CloseHandle(thread); CloseHandle(process); }
        Audit.Write(ProfileDirectory != null ? "profile_created" : "profile_missing", DisplayName);
        return ProfileDirectory != null;
    }

    public void Dispose()
        => _ = CloseAsync();

    internal Task CloseAsync()
    {
        if (secret != null) { Array.Clear(secret); secret = null; }
        // The caller closes its owned application windows first. A card broker also ends its retained
        // token and processes; Local only erases the password held by this object.
        if (card is { } signIn) { card = null; return Task.Run(signIn.Dispose); }
        return Task.CompletedTask;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
