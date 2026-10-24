using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace FidoRun
{
    // The native PIN retry reconstructs an allowlist from its discovery cache.
    // Apply only while the caller's own allowlists were both empty. Preserve
    // PIN protection, HMAC salt handling, encoding, and response validation.
    internal sealed class NativeDiscoveryRequest
    {
        [ThreadStatic] internal static bool DiscoverAll;
        private static NativeDiscoveryRequest root;
        private Encode original, callback;
        private IntPtr bridge;
        private readonly Action<string> log;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int Encode(IntPtr activity,uint kind,uint flags,uint clientFlags,
            IntPtr rp,IntPtr client,IntPtr options,IntPtr device,IntPtr protection,
            IntPtr context,uint prefixSize,IntPtr prefix,IntPtr request,IntPtr size,IntPtr data);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint type,uint protection);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool VirtualProtect(IntPtr address,UIntPtr size,uint protection,out uint old);
        [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(IntPtr process,IntPtr address,UIntPtr size);
        private NativeDiscoveryRequest(Action<string> trace){log=trace;}
        internal static void Install(IntPtr module,Action<string> trace)
        {
            if(root!=null)throw new InvalidOperationException("Discovery adapter already installed.");
            // The owner verifies the complete webauthn.dll SHA256 first.
            IntPtr site=IntPtr.Add(module,0x43775);
            byte[] expected={0xe8,0x26,0x27,0xff,0xff};
            for(int i=0;i<5;i++)if(Marshal.ReadByte(site,i)!=expected[i])throw new NotSupportedException("Unexpected discovery encoding call.");
            var value=new NativeDiscoveryRequest(trace);
            value.original=(Encode)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module,0x35ea0),typeof(Encode));
            value.callback=value.Invoke;
            long start=(site.ToInt64()+0xffff)&~0xffffL;
            for(int i=1;i<8192&&value.bridge==IntPtr.Zero;i++)value.bridge=VirtualAlloc(new IntPtr(start+(long)i*0x10000),(UIntPtr)4096,0x3000,4);
            if(value.bridge==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] jump=new byte[12];jump[0]=0x48;jump[1]=0xb8;
            Array.Copy(BitConverter.GetBytes(Marshal.GetFunctionPointerForDelegate(value.callback).ToInt64()),0,jump,2,8);jump[10]=0xff;jump[11]=0xe0;
            Marshal.Copy(jump,0,value.bridge,12);uint old,ignored;
            if(!VirtualProtect(value.bridge,(UIntPtr)4096,0x20,out old))throw new Win32Exception(Marshal.GetLastWin32Error());
            FlushInstructionCache(new IntPtr(-1),value.bridge,(UIntPtr)12);
            long relative=value.bridge.ToInt64()-site.ToInt64()-5;
            if(relative<int.MinValue||relative>int.MaxValue)throw new InvalidOperationException("Bridge out of range.");
            byte[] call=new byte[5];call[0]=0xe8;Array.Copy(BitConverter.GetBytes((int)relative),0,call,1,4);
            root=value;
            if(!VirtualProtect(site,(UIntPtr)5,0x40,out old))throw new Win32Exception(Marshal.GetLastWin32Error());
            try{Marshal.Copy(call,0,site,5);FlushInstructionCache(new IntPtr(-1),site,(UIntPtr)5);}
            finally{VirtualProtect(site,(UIntPtr)5,old,out ignored);}
        }
        private int Invoke(IntPtr activity,uint kind,uint flags,uint clientFlags,IntPtr rp,IntPtr client,
            IntPtr options,IntPtr device,IntPtr protection,IntPtr context,uint prefixSize,IntPtr prefix,
            IntPtr request,IntPtr size,IntPtr data)
        {
            try
            {
                if(!DiscoverAll||options==IntPtr.Zero)
                    return original(activity,kind,flags,clientFlags,rp,client,options,device,protection,context,prefixSize,prefix,request,size,data);
                int version=Marshal.ReadInt32(options);
                int count=Marshal.ReadInt32(options,8);
                IntPtr modern=version>=4?Marshal.ReadIntPtr(options,80):IntPtr.Zero;
                // Only the known native one-item cache can be expanded here.
                // Explicit caller restrictions never enter DiscoverAll.
                if(count!=1||modern!=IntPtr.Zero)
                    return original(activity,kind,flags,clientFlags,rp,client,options,device,protection,context,prefixSize,prefix,request,size,data);
                log("AccountChoice=NativeCachedFirstCredentialRemoved; CallerAllowlistEmpty=True; NativePinAndHmacHandlingUnchanged=True");
                Marshal.WriteInt32(options,8,0);
                try{return original(activity,kind,flags,clientFlags,rp,client,options,device,protection,context,prefixSize,prefix,request,size,data);}
                finally{Marshal.WriteInt32(options,8,count);}
            }
            catch(Exception e){log("AccountChoice=DiscoveryEncodingFailed; Type="+e.GetType().Name);return unchecked((int)0x80004004);}
        }
    }
}
