using System;
using System.Runtime.InteropServices;

namespace FidoRun
{
    internal static class Status
    {
        public const int Ok = 0;
        public const int False = 1;
        public const int NotImplemented = unchecked((int)0x80004001);
        public const int InvalidArgument = unchecked((int)0x80070057);
        public const int Abort = unchecked((int)0x80004004);
        public static string Hex(int value) { return "0x" + unchecked((uint)value).ToString("X8"); }
        public static void Check(int value, string stage)
        {
            if (value < 0) throw new COMException(stage + " failed: " + Hex(value), value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CredentialSerialization
    {
        public uint AuthenticationPackage;
        public Guid Provider;
        public uint Length;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FieldDescriptor
    {
        public uint Id;
        public int Type;
        public IntPtr Label;
        public Guid FieldType;
    }

    // Method order and GUIDs are from Microsoft's Windows SDK credentialprovider.h.
    [ComImport, Guid("d27c3481-5a1c-45b2-8aaa-c20ebbe8229e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProvider
    {
        [PreserveSig] int SetUsageScenario(int scenario, uint flags);
        [PreserveSig] int SetSerialization(IntPtr serialization);
        [PreserveSig] int Advise(ICredentialProviderEvents events, UIntPtr context);
        [PreserveSig] int UnAdvise();
        [PreserveSig] int GetFieldDescriptorCount(out uint count);
        [PreserveSig] int GetFieldDescriptorAt(uint index, out IntPtr descriptor);
        [PreserveSig] int GetCredentialCount(out uint count, out uint defaultIndex, [MarshalAs(UnmanagedType.Bool)] out bool autoLogon);
        [PreserveSig] int GetCredentialAt(uint index, out ICredentialProviderCredential credential);
    }

    [ComVisible(true), Guid("34201e5a-a787-41a3-a5a4-bd6dcf2a854e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderEvents
    {
        [PreserveSig] int CredentialsChanged(UIntPtr context);
    }

    // Internal Windows notification interface. FIDO also uses this path for
    // key-touch instructions and errors; it is not limited to biometrics.
    // Priority/cancel slots match Microsoft's inspected 26100.9549 FIDO binary.
    [ComVisible(true), Guid("8aa3ef4e-6a82-467d-a159-e0c97d057952")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderBioEvents
    {
        [PreserveSig] int BioFeedbackVisualStateChanged(UIntPtr context, int visualState,
            [MarshalAs(UnmanagedType.LPWStr)] string label,
            [MarshalAs(UnmanagedType.LPWStr)] string accessibility);
        [PreserveSig] int BioFeedbackVisualStateChangedWithPriority(UIntPtr context, int visualState,
            [MarshalAs(UnmanagedType.LPWStr)] string label,
            [MarshalAs(UnmanagedType.LPWStr)] string accessibility, int priority);
        [PreserveSig] int CancelBioFeedbackVisualState(UIntPtr context);
    }

    // Internal Windows interface, absent from the supported SDK surface.
    [ComImport, Guid("a09bcc29-d779-4513-bb59-b4db5d82d2b6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderWithDisplayState
    {
        [PreserveSig] int SetDisplayState(uint flags);
    }

    internal static class DisplayState
    {
        public const uint LegacyVisible = 0x00010081; // Awake, active, provider visible.
        // Internal visibility bit tested by FidoScenarioLogonUI in 26100.9549.
        // Its public symbolic name/contract is not documented.
        public const uint NativeSurface = 0x02000000;
        public static uint ForHost(string mode, bool visible)
        {
            if (mode != "native" && mode != "legacy" && mode != "none")
                throw new ArgumentException("Display state must be native, legacy, or none.");
            if (!visible || mode == "none") return 0;
            return LegacyVisible | (mode == "native" ? NativeSurface : 0U);
        }
    }

    [ComImport, Guid("63913a93-40c1-481a-818d-4072ff8c70cc")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderCredential
    {
        [PreserveSig] int Advise(ICredentialProviderCredentialEvents events);
        [PreserveSig] int UnAdvise();
        [PreserveSig] int SetSelected([MarshalAs(UnmanagedType.Bool)] out bool autoLogon);
        [PreserveSig] int SetDeselected();
        [PreserveSig] int GetFieldState(uint id, out int state, out int interactiveState);
        [PreserveSig] int GetStringValue(uint id, out IntPtr value);
        [PreserveSig] int GetBitmapValue(uint id, out IntPtr bitmap);
        [PreserveSig] int GetCheckboxValue(uint id, [MarshalAs(UnmanagedType.Bool)] out bool value, out IntPtr label);
        [PreserveSig] int GetSubmitButtonValue(uint id, out uint adjacentTo);
        [PreserveSig] int GetComboBoxValueCount(uint id, out uint count, out uint selected);
        [PreserveSig] int GetComboBoxValueAt(uint id, uint index, out IntPtr value);
        [PreserveSig] int SetStringValue(uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetCheckboxValue(uint id, [MarshalAs(UnmanagedType.Bool)] bool value);
        [PreserveSig] int SetComboBoxSelectedValue(uint id, uint selected);
        [PreserveSig] int CommandLinkClicked(uint id);
        [PreserveSig] int GetSerialization(out int response, out CredentialSerialization serialization, out IntPtr statusText, out int statusIcon);
        [PreserveSig] int ReportResult(int status, int subStatus, out IntPtr statusText, out int statusIcon);
    }

    // Explicitly repeat the inherited vtable prefix for .NET COM interop.
    [ComImport, Guid("9387928b-ac75-4bf9-8ab2-2b93c4a55290")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IConnectableCredentialProviderCredential
    {
        [PreserveSig] int Advise(ICredentialProviderCredentialEvents events);
        [PreserveSig] int UnAdvise();
        [PreserveSig] int SetSelected([MarshalAs(UnmanagedType.Bool)] out bool autoLogon);
        [PreserveSig] int SetDeselected();
        [PreserveSig] int GetFieldState(uint id, out int state, out int interactiveState);
        [PreserveSig] int GetStringValue(uint id, out IntPtr value);
        [PreserveSig] int GetBitmapValue(uint id, out IntPtr bitmap);
        [PreserveSig] int GetCheckboxValue(uint id, [MarshalAs(UnmanagedType.Bool)] out bool value, out IntPtr label);
        [PreserveSig] int GetSubmitButtonValue(uint id, out uint adjacentTo);
        [PreserveSig] int GetComboBoxValueCount(uint id, out uint count, out uint selected);
        [PreserveSig] int GetComboBoxValueAt(uint id, uint index, out IntPtr value);
        [PreserveSig] int SetStringValue(uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetCheckboxValue(uint id, [MarshalAs(UnmanagedType.Bool)] bool value);
        [PreserveSig] int SetComboBoxSelectedValue(uint id, uint selected);
        [PreserveSig] int CommandLinkClicked(uint id);
        [PreserveSig] int GetSerialization(out int response, out CredentialSerialization serialization, out IntPtr statusText, out int statusIcon);
        [PreserveSig] int ReportResult(int status, int subStatus, out IntPtr statusText, out int statusIcon);
        [PreserveSig] int Connect(IQueryContinueWithStatus query);
        [PreserveSig] int Disconnect();
    }

    [ComVisible(true), Guid("9090be5b-502b-41fb-bccc-0049a6c7254b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IQueryContinueWithStatus
    {
        [PreserveSig] int QueryContinue();
        [PreserveSig] int SetStatusMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    }

    [ComVisible(true), Guid("7307055c-b24a-486b-9f25-163e597a28a9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IQueryContinue
    {
        [PreserveSig] int QueryContinue();
    }

    [ComVisible(true), Guid("fa6fa76b-66b7-4b11-95f1-86171118e816")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderCredentialEvents
    {
        [PreserveSig] int SetFieldState(ICredentialProviderCredential credential, uint id, int state);
        [PreserveSig] int SetFieldInteractiveState(ICredentialProviderCredential credential, uint id, int state);
        [PreserveSig] int SetFieldString(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetFieldCheckbox(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.Bool)] bool value, [MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int SetFieldBitmap(ICredentialProviderCredential credential, uint id, IntPtr bitmap);
        [PreserveSig] int SetFieldComboBoxSelectedItem(ICredentialProviderCredential credential, uint id, uint index);
        [PreserveSig] int DeleteFieldComboBoxItem(ICredentialProviderCredential credential, uint id, uint index);
        [PreserveSig] int AppendFieldComboBoxItem(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetFieldSubmitButton(ICredentialProviderCredential credential, uint id, uint adjacentTo);
        [PreserveSig] int OnCreatingWindow(out IntPtr owner);
    }

    [ComVisible(true), Guid("b53c00b6-9922-4b78-b1f4-ddfe774dc39b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderCredentialEvents2
    {
        [PreserveSig] int SetFieldState(ICredentialProviderCredential credential, uint id, int state);
        [PreserveSig] int SetFieldInteractiveState(ICredentialProviderCredential credential, uint id, int state);
        [PreserveSig] int SetFieldString(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetFieldCheckbox(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.Bool)] bool value, [MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int SetFieldBitmap(ICredentialProviderCredential credential, uint id, IntPtr bitmap);
        [PreserveSig] int SetFieldComboBoxSelectedItem(ICredentialProviderCredential credential, uint id, uint index);
        [PreserveSig] int DeleteFieldComboBoxItem(ICredentialProviderCredential credential, uint id, uint index);
        [PreserveSig] int AppendFieldComboBoxItem(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetFieldSubmitButton(ICredentialProviderCredential credential, uint id, uint adjacentTo);
        [PreserveSig] int OnCreatingWindow(out IntPtr owner);
        [PreserveSig] int BeginFieldUpdates();
        [PreserveSig] int EndFieldUpdates();
        [PreserveSig] int SetFieldOptions(ICredentialProviderCredential credential, uint id, uint options);
    }

    // Internal Windows interfaces. Full vtable prefixes are intentional.
    // GUID/call sites checked in Microsoft fidocredprov.dll; signatures cross-checked
    // with rbmm/StateMachineState ni.h. These are not supported SDK contracts.
    [ComVisible(true), Guid("2d8deeb8-1322-4973-8df9-b282f2468290")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderCredentialEvents3
    {
        [PreserveSig] int SetFieldState(ICredentialProviderCredential credential, uint id, int state);
        [PreserveSig] int SetFieldInteractiveState(ICredentialProviderCredential credential, uint id, int state);
        [PreserveSig] int SetFieldString(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetFieldCheckbox(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.Bool)] bool value, [MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int SetFieldBitmap(ICredentialProviderCredential credential, uint id, IntPtr bitmap);
        [PreserveSig] int SetFieldComboBoxSelectedItem(ICredentialProviderCredential credential, uint id, uint index);
        [PreserveSig] int DeleteFieldComboBoxItem(ICredentialProviderCredential credential, uint id, uint index);
        [PreserveSig] int AppendFieldComboBoxItem(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetFieldSubmitButton(ICredentialProviderCredential credential, uint id, uint adjacentTo);
        [PreserveSig] int OnCreatingWindow(out IntPtr owner);
        [PreserveSig] int BeginFieldUpdates();
        [PreserveSig] int EndFieldUpdates();
        [PreserveSig] int SetFieldOptions(ICredentialProviderCredential credential, uint id, uint options);
        [PreserveSig] int SetFieldBitmapBuffer(ICredentialProviderCredential credential, uint id, uint bytes, IntPtr buffer);
    }

    [ComVisible(true), Guid("df50ea86-b7a9-4485-8f04-930a49686e5b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderCredentialEvents4
    {
        [PreserveSig] int SetFieldState(ICredentialProviderCredential credential, uint id, int state);
        [PreserveSig] int SetFieldInteractiveState(ICredentialProviderCredential credential, uint id, int state);
        [PreserveSig] int SetFieldString(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetFieldCheckbox(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.Bool)] bool value, [MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int SetFieldBitmap(ICredentialProviderCredential credential, uint id, IntPtr bitmap);
        [PreserveSig] int SetFieldComboBoxSelectedItem(ICredentialProviderCredential credential, uint id, uint index);
        [PreserveSig] int DeleteFieldComboBoxItem(ICredentialProviderCredential credential, uint id, uint index);
        [PreserveSig] int AppendFieldComboBoxItem(ICredentialProviderCredential credential, uint id, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetFieldSubmitButton(ICredentialProviderCredential credential, uint id, uint adjacentTo);
        [PreserveSig] int OnCreatingWindow(out IntPtr owner);
        [PreserveSig] int BeginFieldUpdates();
        [PreserveSig] int EndFieldUpdates();
        [PreserveSig] int SetFieldOptions(ICredentialProviderCredential credential, uint id, uint options);
        [PreserveSig] int SetFieldBitmapBuffer(ICredentialProviderCredential credential, uint id, uint bytes, IntPtr buffer);
        [PreserveSig] int RequestSerialization();
        [PreserveSig] int RequestSelection();
    }

    [ComImport, Guid("095c1484-1c0c-4388-9c6d-500e61bf84bd")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderSetUserArray
    {
        [PreserveSig] int SetUserArray(ICredentialProviderUserArray users);
    }

    [ComVisible(true), Guid("90c119ae-0f18-4520-a1f1-114366a40fe8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICredentialProviderUserArray
    {
        [PreserveSig] int SetProviderFilter(ref Guid provider);
        [PreserveSig] int GetAccountOptions(out int options);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out IntPtr user);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class EmptyUserArray : ICredentialProviderUserArray
    {
        public int SetProviderFilter(ref Guid provider) { return Status.Ok; }
        public int GetAccountOptions(out int options) { options = 3; return Status.Ok; }
        public int GetCount(out uint count) { count = 0; return Status.Ok; }
        public int GetAt(uint index, out IntPtr user) { user = IntPtr.Zero; return Status.InvalidArgument; }
    }
}
