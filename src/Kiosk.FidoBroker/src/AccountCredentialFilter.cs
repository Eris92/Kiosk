using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace FidoRun
{
    // EXPERIMENTAL, opt-in, process-local adapter. No Windows file or other
    // process is modified. The exact OS image hash pins the private 13-slot ABI.
    // This only narrows credential selection; Windows still verifies fresh FIDO
    // assertions and LSA still creates the authenticated token.
    internal sealed class AccountCredentialFilter : IDisposable
    {
        private const string AadHash = "54C163A5FD0B44C191A961A1C50E0A2E2761D9F6843427219835D23640BC1AAF";
        private static AccountCredentialFilter current;
        // Root callbacks for the process lifetime, including callbacks in flight
        // when the native provider is stopping.
        private static readonly System.Collections.Generic.List<AccountCredentialFilter> roots = new System.Collections.Generic.List<AccountCredentialFilter>();
        private byte[] credential;
        private NativeAccountSelector selector;
        private string selectedLabel;
        public static string SelectedAccountLabel { get { return current==null ? null : current.selectedLabel; } }
        private readonly Action<string> log;
        private NativeAssertion original, callback;
        private IntPtr slot, previous;
        private int matched;
        private bool disposed;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int NativeAssertion(IntPtr cancel, uint flags, IntPtr rp, IntPtr client,
            IntPtr options, IntPtr assertion, IntPtr activity, IntPtr appId, IntPtr pin,
            IntPtr hmac, IntPtr device, uint prefixLength, IntPtr prefix);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint old);

        private AccountCredentialFilter(byte[] selected, Action<string> trace)
        {
            if (selected != null && (selected.Length == 0 || selected.Length > 4096))
                throw new ArgumentException("Invalid selected credential ID.");
            credential = selected==null ? null : (byte[])selected.Clone(); log = trace;
        }

        public static AccountCredentialFilter Install(string selectedBase64Url, Action<string> trace)
        {
            string encoded = selectedBase64Url.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            return InstallCore(Convert.FromBase64String(encoded),trace,null);
        }
        public static AccountCredentialFilter InstallAutomatic(Func<System.Collections.Generic.List<KeyAccount>,int> choose,Action<string> trace)
        { return InstallCore(null,trace,choose); }
        private static AccountCredentialFilter InstallCore(byte[] selected,Action<string> trace,Func<System.Collections.Generic.List<KeyAccount>,int> choose)
        {
            var filter = new AccountCredentialFilter(selected, trace);
            if (current != null) throw new InvalidOperationException("An account choice is already active.");
            string path = Path.Combine(Environment.SystemDirectory, "aadauthhelper.dll");
            using (var hash = SHA256.Create())
            using (var file = File.OpenRead(path))
                if (BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "") != AadHash)
                    throw new NotSupportedException("The account adapter has not been verified for this Windows DLL version.");
            IntPtr aad = LoadLibraryExW(path, IntPtr.Zero, 0x800);
            IntPtr web = LoadLibraryExW(Path.Combine(Environment.SystemDirectory,"webauthn.dll"), IntPtr.Zero, 0x800);
            if (aad == IntPtr.Zero || web == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr entry = GetProcAddress(web, "WebAuthNCtapGetAssertion");
            if (entry == IntPtr.Zero) throw new NotSupportedException("Native CTAP assertion entry is unavailable.");
            filter.original = (NativeAssertion)Marshal.GetDelegateForFunctionPointer(entry, typeof(NativeAssertion));
            filter.callback = filter.Filter;
            filter.slot = FindImport(aad);
            filter.previous = Marshal.ReadIntPtr(filter.slot);
            lock (roots) roots.Add(filter);
            if(selected==null)filter.selector=NativeAccountSelector.Install(choose,filter.BindNativeChoice,trace);
            filter.ReplaceSlot(Marshal.GetFunctionPointerForDelegate(filter.callback));
            current = filter;
            trace("AccountChoice=Bound; Scope=OwnProcess; CredentialIdsLogged=False; WindowsSignatureAndLsaValidation=Unchanged");
            return filter;
        }
        private void BindNativeChoice(KeyAccount choice)
        {
            if(credential!=null && !SameBytes(credential,choice.CredentialId))throw new InvalidOperationException("The chosen credential changed within this logon.");
            credential=(byte[])choice.CredentialId.Clone();selectedLabel=choice.Name;
            log("AccountChoice=SelectedAfterNativePin; IdentifierLogging=False");
        }
        private static bool SameBytes(byte[] a,byte[] b)
        { if(a.Length!=b.Length)return false;int d=0;for(int i=0;i<a.Length;i++)d|=a[i]^b[i];return d==0; }
        private int ConfirmResponse(int result,IntPtr output)
        {
            if(result<0)return result;
            IntPtr response=Marshal.ReadIntPtr(output);
            if(response==IntPtr.Zero)return unchecked((int)0x8009030c);
            int count=Marshal.ReadInt32(response,36);IntPtr bytes=Marshal.ReadIntPtr(response,40);
            if(credential==null)
            {
                if(count<1||count>4096||bytes==IntPtr.Zero)return unchecked((int)0x8009030c);
                credential=new byte[count];Marshal.Copy(bytes,credential,0,count);
                log("AccountChoice=SingleNativeAccount; PickerShown=False");
            }
            if(!Matches(count,bytes))return unchecked((int)0x8009030c);
            Interlocked.Increment(ref matched);
            log("AccountChoice=NativeAssertionMatched; WindowsLogonNotYetEstablished=True");
            return result;
        }

        private static IntPtr FindImport(IntPtr module)
        {
            if (IntPtr.Size != 8 || Marshal.ReadInt16(module) != 0x5a4d) throw new NotSupportedException("Expected an x64 PE image.");
            int nt = Marshal.ReadInt32(module, 0x3c);
            if (nt < 64 || nt > 4096 || Marshal.ReadInt32(module,nt) != 0x4550) throw new InvalidDataException("Invalid PE header.");
            int optional = nt + 24;
            if (Marshal.ReadInt16(module,optional) != 0x20b) throw new NotSupportedException("Expected PE32+.");
            int imageSize = Marshal.ReadInt32(module,optional + 56);
            int delay = Marshal.ReadInt32(module,optional + 112 + 13 * 8);
            int delaySize = Marshal.ReadInt32(module,optional + 116 + 13 * 8);
            for (int offset = 0; offset + 32 <= delaySize; offset += 32)
            {
                IntPtr descriptor = Rva(module,delay+offset,32,imageSize);
                int attrs=Marshal.ReadInt32(descriptor), name=Marshal.ReadInt32(descriptor,4);
                if (name == 0) break;
                if (attrs != 1) throw new NotSupportedException("Expected RVA delay imports.");
                if (!String.Equals(Marshal.PtrToStringAnsi(Rva(module,name,64,imageSize)), "webauthn.dll",StringComparison.OrdinalIgnoreCase)) continue;
                int iat=Marshal.ReadInt32(descriptor,12), names=Marshal.ReadInt32(descriptor,16);
                for (int index=0; index<2048; index++)
                {
                    long entry=Marshal.ReadInt64(Rva(module,names+index*8,8,imageSize));
                    if (entry==0) break;
                    if (entry<0 || entry>Int32.MaxValue) continue;
                    string function=Marshal.PtrToStringAnsi(Rva(module,(int)entry+2,64,imageSize));
                    if (function=="WebAuthNCtapGetAssertion") return Rva(module,iat+index*8,8,imageSize);
                }
            }
            throw new NotSupportedException("The expected private import was not found.");
        }
        private static IntPtr Rva(IntPtr module,int offset,int length,int size)
        {
            if (offset<0 || length<0 || offset>size-length) throw new InvalidDataException("Invalid PE range.");
            return IntPtr.Add(module,offset);
        }
        private void ReplaceSlot(IntPtr value)
        {
            if (slot.ToInt64()%8 != 0) throw new InvalidDataException("Unaligned import slot.");
            uint old, ignored;
            if (!VirtualProtect(slot,(UIntPtr)8,4,out old)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.WriteIntPtr(slot,value); Thread.MemoryBarrier(); }
            finally { if (!VirtualProtect(slot,(UIntPtr)8,old,out ignored)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }

        private int Filter(IntPtr cancel,uint flags,IntPtr rp,IntPtr client,IntPtr options,
            IntPtr assertion,IntPtr activity,IntPtr appId,IntPtr pin,IntPtr hmac,IntPtr device,uint prefixLength,IntPtr prefix)
        {
            try
            {
                Interlocked.Exchange(ref matched,0);
                log("AccountChoice=NativeRequestObserved; OptionsVersion=" + (options==IntPtr.Zero ? 0 : Marshal.ReadInt32(options)));
                if (disposed || options==IntPtr.Zero || assertion==IntPtr.Zero ||
                    !String.Equals(Marshal.PtrToStringUni(rp),"login.microsoft.com",StringComparison.OrdinalIgnoreCase))
                    return unchecked((int)0x80004004);
                int version=Marshal.ReadInt32(options);
                if (version<1 || version>9) return unchecked((int)0x80004001);
                if(credential==null)
                {
                    int verification=Marshal.ReadInt32(options,44);
                    int attachment=Marshal.ReadInt32(options,40);
                    uint discoveryFlags=flags & ~0x10008000u;
                    log("AccountChoice=NativeDiscovery; Flags=0x"+discoveryFlags.ToString("X8")+"; AllowedCount="+Marshal.ReadInt32(options,8)+"; PrefixLength="+prefixLength+"; DiscoveryPrefixLength=3; UserVerification=Required");
                    // A discoverable assertion must verify the user before the
                    // key can return account names. Never start a silent probe
                    // which can narrow the operation to the first credential.
                    Marshal.WriteInt32(options,44,1);
                    Marshal.WriteInt32(options,40,2);
                    NativeDiscoveryRequest.DiscoverAll=Marshal.ReadInt32(options,8)==0 && (version<4 || Marshal.ReadIntPtr(options,80)==IntPtr.Zero);
                    try{return ConfirmResponse(original(cancel,discoveryFlags,rp,client,options,assertion,activity,appId,pin,hmac,device,prefixLength>=3 ? 3u : prefixLength,prefix),assertion);}
                    finally{NativeDiscoveryRequest.DiscoverAll=false;Marshal.WriteInt32(options,44,verification);Marshal.WriteInt32(options,40,attachment);}
                }
                int oldCount=Marshal.ReadInt32(options,8);
                IntPtr oldItems=Marshal.ReadIntPtr(options,16);
                IntPtr oldModern=version>=4 ? Marshal.ReadIntPtr(options,80) : IntPtr.Zero;
                // Never widen a native allowlist. Existing lists must also contain
                // this exact credential before we narrow them to a single item.
                if (!ContainsSelected(oldCount,oldItems,24,false)) return unchecked((int)0x8009030c);
                if (oldModern!=IntPtr.Zero && !ContainsSelected(Marshal.ReadInt32(oldModern),Marshal.ReadIntPtr(oldModern,8),0,true))
                    return unchecked((int)0x8009030c);
                IntPtr id=Marshal.AllocHGlobal(credential.Length), item=Marshal.AllocHGlobal(24), type=Marshal.StringToHGlobalUni("public-key");
                try
                {
                    Marshal.Copy(credential,0,id,credential.Length);
                    Marshal.WriteInt32(item,1); Marshal.WriteInt32(item,4,credential.Length);
                    Marshal.WriteIntPtr(item,8,id); Marshal.WriteIntPtr(item,16,type);
                    Marshal.WriteInt32(options,8,1); Marshal.WriteIntPtr(options,16,item);
                    if (version>=4) Marshal.WriteIntPtr(options,80,IntPtr.Zero);
                    int result;
                    try { result=original(cancel,flags,rp,client,options,assertion,activity,appId,pin,hmac,device,prefixLength,prefix); }
                    finally
                    {
                        Marshal.WriteInt32(options,8,oldCount); Marshal.WriteIntPtr(options,16,oldItems);
                        if (version>=4) Marshal.WriteIntPtr(options,80,oldModern);
                    }
                    return ConfirmResponse(result,assertion);
                }
                finally { Marshal.FreeHGlobal(type); Marshal.FreeHGlobal(item); Marshal.FreeHGlobal(id); }
            }
            catch (Exception e) { log("AccountChoice=AdapterFailed; Type="+e.GetType().Name); return unchecked((int)0x80004004); }
        }
        private bool ContainsSelected(int count,IntPtr items,int stride,bool indirect)
        {
            if (count==0) return true;
            if (count<0 || count>256 || items==IntPtr.Zero) return false;
            for (int i=0;i<count;i++)
            {
                IntPtr item=indirect ? Marshal.ReadIntPtr(items,i*8) : IntPtr.Add(items,i*stride);
                if (item!=IntPtr.Zero && Matches(Marshal.ReadInt32(item,4),Marshal.ReadIntPtr(item,8))) return true;
            }
            return false;
        }
        private bool Matches(int count,IntPtr bytes)
        {
            if (count!=credential.Length || bytes==IntPtr.Zero) return false;
            int difference=0;
            for (int i=0;i<count;i++) difference |= credential[i]^Marshal.ReadByte(bytes,i);
            return difference==0;
        }
        public static void RequireBoundSelection()
        {
            if (current==null || current.disposed || Volatile.Read(ref current.matched)==0)
                throw new InvalidOperationException("The native logon did not use the selected credential. No LSA submission is allowed.");
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed=true;
            if(selector!=null)selector.Stop();
            ReplaceSlot(previous);
            if (current==this) current=null;
        }
    }
}
