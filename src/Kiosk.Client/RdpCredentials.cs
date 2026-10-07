using System.Runtime.InteropServices;

namespace Kiosk.Client;

// Native IMsRdpClientNonScriptable5 is IUnknown-based, not IDispatch.
// Slots verified against the Windows mstscax.dll type library. Gaps preserve
// the inherited vtable without exposing unrelated credential/password methods.
[ComImport, Guid("4f6996d5-d7b1-412c-b0ff-063718566907"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRdpCredentials
{
    void _VtblGap1_16();
    void SetPromptForCredentials([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetPromptForCredentials();
    void _VtblGap2_24();
    void SetAllowCredentialSaving([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetAllowCredentialSaving();
    void SetPromptForCredsOnClient([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetPromptForCredsOnClient();
    void _VtblGap3_14();
    void SetAllowPromptingForCredentials([MarshalAs(UnmanagedType.VariantBool)] bool value);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool GetAllowPromptingForCredentials();
}
