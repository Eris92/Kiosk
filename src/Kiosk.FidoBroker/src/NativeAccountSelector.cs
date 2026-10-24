using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FidoRun
{
    internal sealed class KeyAccount
    {
        public int Index;
        public byte[] CredentialId, UserId;
        public string Name;
        public override string ToString() { return Name; }
    }

    // Called inside the native decoding operation, AFTER the key PIN and touch.
    // Reuses Windows' unmodified decoder/encoder for the chosen response.
    // The hook changes only a single call instruction in this process. Its code
    // and callback remain allocated until process exit to cover native workers.
    internal sealed class NativeAccountSelector
    {
        private const string WebHash="D1758AF588B376C01302274281BE260AB9DE7B2AE77E1DCD10E6F3A68B218309";
        private static NativeAccountSelector root;
        private readonly Func<List<KeyAccount>,int> choose;
        private readonly Action<KeyAccount> bind;
        private readonly Action<string> log;
        private SelectResponse original,callback;
        private IntPtr bridge;
        private bool active=true;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int SelectResponse(IntPtr activity,uint index,uint size,IntPtr data,
            uint prefixSize,IntPtr prefix,IntPtr outputSize,IntPtr output);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
        private static extern IntPtr LoadLibraryExW(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        private static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint type,uint protection);
        [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)]
        private static extern bool VirtualProtect(IntPtr address,UIntPtr size,uint protection,out uint old);
        [DllImport("kernel32.dll",ExactSpelling=true)]
        private static extern bool FlushInstructionCache(IntPtr process,IntPtr address,UIntPtr length);

        private NativeAccountSelector(Func<List<KeyAccount>,int> chooser,Action<KeyAccount> binding,Action<string> trace)
        { choose=chooser;bind=binding;log=trace; }
        public static NativeAccountSelector Install(Func<List<KeyAccount>,int> chooser,Action<KeyAccount> binding,Action<string> trace)
        {
            if(root!=null)throw new InvalidOperationException("Only one native account selector per process is supported.");
            string path=Path.Combine(Environment.SystemDirectory,"webauthn.dll");
            using(var sha=SHA256.Create())using(var file=File.OpenRead(path))
                if(BitConverter.ToString(sha.ComputeHash(file)).Replace("-","")!=WebHash)
                    throw new NotSupportedException("This Windows WebAuthn image has not been verified for single-PIN selection.");
            IntPtr module=LoadLibraryExW(path,IntPtr.Zero,0x800);
            if(module==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeDiscoveryRequest.Install(module,trace);
            var value=new NativeAccountSelector(chooser,binding,trace);
            value.original=(SelectResponse)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module,0x39b78),typeof(SelectResponse));
            value.callback=value.Select;
            IntPtr site=IntPtr.Add(module,0x33263);
            byte[] expected={0xe8,0x10,0x69,0,0};
            for(int i=0;i<expected.Length;i++)if(Marshal.ReadByte(site,i)!=expected[i])throw new NotSupportedException("Unexpected native selection call.");
            // A direct relative call needs a bridge within 2 GiB. Allocate one
            // page RW, populate it, then switch to RX before publishing the call.
            long start=(site.ToInt64()+0xffff)&~0xffffL;
            for(int i=1;i<8192 && value.bridge==IntPtr.Zero;i++)
            {
                long address=start+(long)i*0x10000;
                value.bridge=VirtualAlloc(new IntPtr(address),(UIntPtr)4096,0x3000,4);
            }
            if(value.bridge==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot allocate process-local selection bridge.");
            byte[] jump=new byte[12];jump[0]=0x48;jump[1]=0xb8;
            Array.Copy(BitConverter.GetBytes(Marshal.GetFunctionPointerForDelegate(value.callback).ToInt64()),0,jump,2,8);
            jump[10]=0xff;jump[11]=0xe0;Marshal.Copy(jump,0,value.bridge,jump.Length);
            uint old,ignored;
            if(!VirtualProtect(value.bridge,(UIntPtr)4096,0x20,out old))throw new Win32Exception(Marshal.GetLastWin32Error());
            FlushInstructionCache(new IntPtr(-1),value.bridge,(UIntPtr)jump.Length);
            long displacement=value.bridge.ToInt64()-(site.ToInt64()+5);
            if(displacement<Int32.MinValue||displacement>Int32.MaxValue)throw new InvalidOperationException("Selection bridge out of range.");
            byte[] call=new byte[5];call[0]=0xe8;Array.Copy(BitConverter.GetBytes((int)displacement),0,call,1,4);
            root=value; // Root before any native thread can reach the callback.
            if(!VirtualProtect(site,(UIntPtr)5,0x40,out old))throw new Win32Exception(Marshal.GetLastWin32Error());
            try{Marshal.Copy(call,0,site,call.Length);FlushInstructionCache(new IntPtr(-1),site,(UIntPtr)5);}
            finally{if(!VirtualProtect(site,(UIntPtr)5,old,out ignored))throw new Win32Exception(Marshal.GetLastWin32Error());}
            trace("AccountChoice=SinglePinAdapterReady; Scope=OwnProcess; PinStorage=NativeProviderOnly");
            return value;
        }
        public void Stop(){active=false;}
        private int Select(IntPtr activity,uint oldIndex,uint size,IntPtr data,uint prefixSize,
            IntPtr prefix,IntPtr outputSize,IntPtr output)
        {
            try
            {
                if(!active)return unchecked((int)0x80004004);
                List<KeyAccount> all=AccountMetadata.Read(data,size);
                var available=new List<KeyAccount>();
                foreach(var account in all)
                {
                    bool allowed=prefixSize==0;
                    if(prefixSize>0 && prefix!=IntPtr.Zero && prefixSize<=account.UserId.Length)
                    {
                        allowed=true;
                        for(int i=0;i<prefixSize;i++)if(Marshal.ReadByte(prefix,i)!=account.UserId[i]){allowed=false;break;}
                    }
                    if(allowed)available.Add(account);
                }
                if(available.Count==0)return unchecked((int)0x8009030c);
                log("AccountChoice=NativeAccountsAvailable; Count="+available.Count+"; NativeKeyResponsesReceived=True; AdditionalPinRequested=False");
                int selected=available.Count==1 ? 0 : choose(available);
                if(selected<0||selected>=available.Count)return unchecked((int)0x80004004);
                KeyAccount choice=available[selected];
                // Narrow the original user prefix to this complete user handle,
                // preventing the original native helper choosing its first match.
                IntPtr fullId=Marshal.AllocHGlobal(choice.UserId.Length);
                try
                {
                    Marshal.Copy(choice.UserId,0,fullId,choice.UserId.Length);
                    int result=original(activity,(uint)choice.Index,size,data,(uint)choice.UserId.Length,fullId,outputSize,output);
                    if(result>=0)bind(choice);
                    return result;
                }
                finally{Marshal.FreeHGlobal(fullId);}
            }
            catch(Exception e){log("AccountChoice=NativeSelectionFailed; Type="+e.GetType().Name);return unchecked((int)0x80004004);}
        }
    }

    // Bounded, metadata-only CTAP CBOR reader. Signature/authenticator/HMAC
    // values are skipped in their native buffer, never copied into managed data.
    internal sealed class AccountMetadata
    {
        private readonly IntPtr data;private readonly int length;private int position;
        private AccountMetadata(IntPtr input,int size){data=input;length=size;}
        private byte Byte(){if(position>=length)throw new InvalidDataException("Truncated CBOR.");return Marshal.ReadByte(data,position++);}
        private ulong Head(out int major)
        {
            byte value=Byte();major=value>>5;int extra=value&31;
            if(extra<24)return (ulong)extra;
            int bytes=extra==24?1:extra==25?2:extra==26?4:extra==27?8:0;
            if(bytes==0)throw new InvalidDataException("Indefinite CBOR is not supported.");
            ulong count=0;for(int i=0;i<bytes;i++)count=(count<<8)|Byte();return count;
        }
        private int Count(int type,int limit){int major;ulong count=Head(out major);if(major!=type||count>(ulong)limit)throw new InvalidDataException("Invalid CBOR field.");return (int)count;}
        private byte[] Bytes(){int count=Count(2,4096);if(count>length-position)throw new InvalidDataException();byte[] result=new byte[count];Marshal.Copy(IntPtr.Add(data,position),result,0,count);position+=count;return result;}
        private string Text(){int count=Count(3,4096);if(count>length-position)throw new InvalidDataException();byte[] value=new byte[count];Marshal.Copy(IntPtr.Add(data,position),value,0,count);position+=count;return new UTF8Encoding(false,true).GetString(value);}
        private void Skip(int depth)
        {
            if(depth>16)throw new InvalidDataException("CBOR depth exceeded.");
            int type;ulong count=Head(out type);
            if(type==2||type==3){if(count>(ulong)(length-position))throw new InvalidDataException();position+=(int)count;}
            else if(type==4||type==5){if(count>4096)throw new InvalidDataException();ulong n=count*(type==5?2u:1u);for(ulong i=0;i<n;i++)Skip(depth+1);}
            else if(type==6)Skip(depth+1);
        }
        public static List<KeyAccount> Read(IntPtr data,uint size)
        {
            if(data==IntPtr.Zero||size==0||size>1024*1024)throw new InvalidDataException("Invalid CTAP response size.");
            var reader=new AccountMetadata(data,(int)size);var result=new List<KeyAccount>();
            int total=reader.Count(4,256);
            for(int index=0;index<total;index++)
            {
                var account=new KeyAccount{Index=index};int fields=reader.Count(5,64);
                for(int f=0;f<fields;f++)
                {
                    int key=reader.Count(0,Int32.MaxValue);
                    if(key==1||key==4)
                    {
                        int properties=reader.Count(5,32);
                        for(int p=0;p<properties;p++)
                        {
                            string name=reader.Text();
                            if(name=="id"){if(key==1)account.CredentialId=reader.Bytes();else account.UserId=reader.Bytes();}
                            else if(key==4&&name=="name")account.Name=TraceLog.DisplayText(reader.Text());
                            else reader.Skip(0);
                        }
                    }
                    else reader.Skip(0);
                }
                if(account.CredentialId==null||account.CredentialId.Length==0||account.UserId==null||account.UserId.Length==0)
                    throw new InvalidDataException("Account response has no binding identifiers.");
                if(String.IsNullOrWhiteSpace(account.Name))account.Name="Konto "+(index+1);
                result.Add(account);
            }
            if(reader.position!=reader.length)throw new InvalidDataException("Trailing CBOR data.");
            return result;
        }
    }
}
