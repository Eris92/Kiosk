using System.Diagnostics;

namespace FidoRun
{
    /// <summary>The broker's PID and Windows session cannot change while it runs.</summary>
    internal static class ProcessIdentity
    {
        internal static readonly int Id;
        internal static readonly int SessionId;

        static ProcessIdentity()
        {
            using (Process current = Process.GetCurrentProcess())
            {
                Id = current.Id;
                SessionId = current.SessionId;
            }
        }
    }
}
