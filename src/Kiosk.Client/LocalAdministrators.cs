using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Kiosk.Client;

/// <summary>
/// Is the account on a card a member of this PC's local Administrators group? Members are listed as
/// DOMAIN\user (AD: INVESTA\k.lechmyc, Entra ID: AzureAD\JanKowalski) and compared with the card's UPN.
/// Accounts that are administrators only through a nested group or an Entra role are not detected;
/// add such cards to the Kiosk's own administrator list instead.
/// </summary>
internal static class LocalAdministrators
{
    internal static bool Contains(string upn)
    {
        var at = upn.IndexOf('@');
        if (at <= 0) return false;
        string user = upn[..at], domain = upn[(at + 1)..];
        try { return Members().Any(member => Matches(member, user, domain)); }
        catch (Exception ex) { Audit.Write("admin_check_failed", ex.Message); return false; }
    }

    internal static bool Matches(string member, string user, string domain)
    {
        var slash = member.IndexOf('\\');
        if (slash <= 0) return false;
        string memberDomain = member[..slash], memberUser = member[(slash + 1)..];
        if (!memberUser.Equals(user, StringComparison.OrdinalIgnoreCase)) return false;
        // investa.pl ↔ INVESTA (the NetBIOS name is normally the first DNS label); AzureAD ↔ AzureAD.
        return memberDomain.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
               memberDomain.Equals(domain.Split('.')[0], StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> Members()
    {
        var group = ((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount))).Value;
        group = group[(group.IndexOf('\\') + 1)..]; // Localised, e.g. "Administratorzy".
        var members = new List<string>();
        IntPtr resume = IntPtr.Zero;
        int status = NetLocalGroupGetMembers(null, group, 3, out var buffer, -1, out var read, out _, ref resume);
        try
        {
            if (status != 0) throw new System.ComponentModel.Win32Exception(status);
            for (int i = 0; i < read; i++)
                members.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size)) ?? "");
        }
        finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        return members;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer, int maxLength, out int read, out int total, ref IntPtr resume);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);
}
