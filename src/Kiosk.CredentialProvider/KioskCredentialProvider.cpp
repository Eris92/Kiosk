// Kiosk sign-in tile: a credential provider that wraps the Windows security key (FIDO2) provider.
// Windows' own provider still performs the Entra ID sign-in (and so SSO); this wrapper only replaces
// the tile picture and title with the Kiosk ones. Everything else is passed through unchanged.
//
// Configuration (written by scripts\Install-KioskCredentialProvider.ps1):
//   HKLM\SOFTWARE\Kiosk\CredentialProvider  Title (REG_SZ), TileImage (REG_SZ, .bmp), InnerProvider (REG_SZ, CLSID)
// If the wrapped provider cannot be created the tile is simply not shown; other sign-in options stay.

#include <windows.h>
#include <initguid.h>
#include <credentialprovider.h>
#include <shlwapi.h>
#include <new>
#include <vector>

// {7A4D2C1E-5B3F-4E8A-9C6D-2F1B8E4A7C30}
DEFINE_GUID(CLSID_KioskProvider, 0x7a4d2c1e, 0x5b3f, 0x4e8a, 0x9c, 0x6d, 0x2f, 0x1b, 0x8e, 0x4a, 0x7c, 0x30);
// Windows "FIDO Credential Provider" (security key sign-in).
static const wchar_t DefaultInner[] = L"{F8A1793B-7873-4046-B2A7-1F318747F427}";
static const wchar_t SettingsKey[] = L"SOFTWARE\\Kiosk\\CredentialProvider";

static LONG g_objects = 0, g_locks = 0;
static HINSTANCE g_module = nullptr;

static void ReadSetting(const wchar_t* name, wchar_t* value, DWORD chars, const wchar_t* fallback)
{
    DWORD bytes = chars * sizeof(wchar_t);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, SettingsKey, name, RRF_RT_REG_SZ, nullptr, value, &bytes) != ERROR_SUCCESS)
        lstrcpynW(value, fallback, chars);
}

template <class T> static void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

/// Diagnostics for testing on the sign-in screen: with Trace = 1 in the settings key, calls are appended to
/// C:\ProgramData\Kiosk\signin-trace.log. Typed values (the PIN) are never written, only their field number.
static void Trace(const wchar_t* format, ...)
{
    DWORD enabled = 0, size = sizeof(enabled);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, SettingsKey, L"Trace", RRF_RT_REG_DWORD, nullptr, &enabled, &size) != ERROR_SUCCESS || !enabled) return;
    wchar_t text[600];
    SYSTEMTIME now;
    GetLocalTime(&now);
    int prefix = swprintf_s(text, L"%02u:%02u:%02u.%03u [%lu] ", now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, GetCurrentThreadId());
    va_list args;
    va_start(args, format);
    _vsnwprintf_s(text + prefix, ARRAYSIZE(text) - prefix - 2, _TRUNCATE, format, args);
    va_end(args);
    lstrcatW(text, L"\r\n");
    char utf8[1800];
    int bytes = WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, sizeof(utf8), nullptr, nullptr) - 1;
    HANDLE file = CreateFileW(L"C:\\ProgramData\\Kiosk\\signin-trace.log", FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written;
    if (bytes > 0) WriteFile(file, utf8, bytes, &written, nullptr);
    CloseHandle(file);
}

// ------------------------------------------------------------------ credential events

class Credential;

/// Windows identifies a credential by its pointer; the wrapped credential reports itself, so its events
/// are re-sent with the wrapper as the sender.
class Events final : public ICredentialProviderCredentialEvents2
{
    LONG refs = 1;
    ICredentialProviderCredentialEvents* outer;
    ICredentialProviderCredentialEvents2* outer2 = nullptr;
    ICredentialProviderCredential* sender;
public:
    Events(ICredentialProviderCredentialEvents* events, ICredentialProviderCredential* wrapper) : outer(events), sender(wrapper)
    {
        outer->AddRef();
        outer->QueryInterface(IID_PPV_ARGS(&outer2));
        InterlockedIncrement(&g_objects);
    }
    ~Events() { SafeRelease(outer2); SafeRelease(outer); InterlockedDecrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == __uuidof(ICredentialProviderCredentialEvents)) *ppv = static_cast<ICredentialProviderCredentialEvents*>(this);
        else if (riid == __uuidof(ICredentialProviderCredentialEvents2) && outer2) *ppv = static_cast<ICredentialProviderCredentialEvents2*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs); }
    IFACEMETHODIMP_(ULONG) Release() override { LONG r = InterlockedDecrement(&refs); if (!r) delete this; return r; }

    IFACEMETHODIMP SetFieldState(ICredentialProviderCredential*, DWORD f, CREDENTIAL_PROVIDER_FIELD_STATE s) override { HRESULT hr = outer->SetFieldState(sender, f, s); Trace(L"event SetFieldState %lu=%d -> %08lX", f, s, hr); return hr; }
    IFACEMETHODIMP SetFieldInteractiveState(ICredentialProviderCredential*, DWORD f, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE s) override { HRESULT hr = outer->SetFieldInteractiveState(sender, f, s); Trace(L"event SetFieldInteractiveState %lu=%d -> %08lX", f, s, hr); return hr; }
    IFACEMETHODIMP SetFieldString(ICredentialProviderCredential*, DWORD f, LPCWSTR s) override;
    IFACEMETHODIMP SetFieldCheckbox(ICredentialProviderCredential*, DWORD f, BOOL c, LPCWSTR s) override { return outer->SetFieldCheckbox(sender, f, c, s); }
    IFACEMETHODIMP SetFieldBitmap(ICredentialProviderCredential*, DWORD f, HBITMAP b) override;
    IFACEMETHODIMP SetFieldComboBoxSelectedItem(ICredentialProviderCredential*, DWORD f, DWORD i) override { return outer->SetFieldComboBoxSelectedItem(sender, f, i); }
    IFACEMETHODIMP DeleteFieldComboBoxItem(ICredentialProviderCredential*, DWORD f, DWORD i) override { return outer->DeleteFieldComboBoxItem(sender, f, i); }
    IFACEMETHODIMP AppendFieldComboBoxItem(ICredentialProviderCredential*, DWORD f, LPCWSTR s) override { return outer->AppendFieldComboBoxItem(sender, f, s); }
    IFACEMETHODIMP SetFieldSubmitButton(ICredentialProviderCredential*, DWORD f, DWORD a) override { Trace(L"event SetFieldSubmitButton %lu next to %lu", f, a); return outer->SetFieldSubmitButton(sender, f, a); }
    IFACEMETHODIMP OnCreatingWindow(HWND* hwnd) override { HRESULT hr = outer->OnCreatingWindow(hwnd); Trace(L"event OnCreatingWindow -> %08lX", hr); return hr; }
    IFACEMETHODIMP BeginFieldUpdates() override { return outer2 ? outer2->BeginFieldUpdates() : E_NOTIMPL; }
    IFACEMETHODIMP EndFieldUpdates() override { return outer2 ? outer2->EndFieldUpdates() : E_NOTIMPL; }
    IFACEMETHODIMP SetFieldOptions(ICredentialProviderCredential*, DWORD f, CREDENTIAL_PROVIDER_CREDENTIAL_FIELD_OPTIONS o) override
    {
        return outer2 ? outer2->SetFieldOptions(sender, f, o) : E_NOTIMPL;
    }
};

// ------------------------------------------------------------------ credential

class Credential final : public ICredentialProviderCredential2, public ICredentialProviderCredentialWithFieldOptions
{
    LONG refs = 1;
    ICredentialProviderCredential* inner;
    ICredentialProviderCredential2* inner2 = nullptr;
    ICredentialProviderCredentialWithFieldOptions* innerOptions = nullptr;
    Events* events = nullptr;
public:
    const DWORD titleField, imageField; // MAXDWORD when the wrapped tile has no such field.
    Credential(ICredentialProviderCredential* wrapped, DWORD title, DWORD image) : inner(wrapped), titleField(title), imageField(image)
    {
        inner->AddRef();
        inner->QueryInterface(IID_PPV_ARGS(&inner2));
        inner->QueryInterface(IID_PPV_ARGS(&innerOptions));
        InterlockedIncrement(&g_objects);
    }
    ~Credential() { SafeRelease(events); SafeRelease(innerOptions); SafeRelease(inner2); SafeRelease(inner); InterlockedDecrement(&g_objects); }
    ICredentialProviderCredential* Inner() const { return inner; }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == __uuidof(ICredentialProviderCredential)) *ppv = static_cast<ICredentialProviderCredential*>(static_cast<ICredentialProviderCredential2*>(this));
        else if (riid == __uuidof(ICredentialProviderCredential2) && inner2) *ppv = static_cast<ICredentialProviderCredential2*>(this);
        else if (riid == __uuidof(ICredentialProviderCredentialWithFieldOptions) && innerOptions) *ppv = static_cast<ICredentialProviderCredentialWithFieldOptions*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs); }
    IFACEMETHODIMP_(ULONG) Release() override { LONG r = InterlockedDecrement(&refs); if (!r) delete this; return r; }

    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* pcpce) override
    {
        SafeRelease(events);
        if (!pcpce) return inner->Advise(nullptr);
        events = new (std::nothrow) Events(pcpce, static_cast<ICredentialProviderCredential2*>(this));
        if (!events) return E_OUTOFMEMORY;
        return inner->Advise(events);
    }
    IFACEMETHODIMP UnAdvise() override { HRESULT hr = inner->UnAdvise(); SafeRelease(events); return hr; }
    IFACEMETHODIMP SetSelected(BOOL* autoLogon) override { HRESULT hr = inner->SetSelected(autoLogon); Trace(L"SetSelected -> %08lX auto %d", hr, autoLogon ? *autoLogon : -1); return hr; }
    IFACEMETHODIMP SetDeselected() override { Trace(L"SetDeselected"); return inner->SetDeselected(); }
    IFACEMETHODIMP GetFieldState(DWORD f, CREDENTIAL_PROVIDER_FIELD_STATE* s, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* i) override { HRESULT hr = inner->GetFieldState(f, s, i); Trace(L"GetFieldState %lu -> %08lX state %d interactive %d", f, hr, s ? *s : -1, i ? *i : -1); return hr; }
    IFACEMETHODIMP GetStringValue(DWORD f, LPWSTR* value) override
    {
        if (f == titleField)
        {
            wchar_t title[256];
            ReadSetting(L"Title", title, ARRAYSIZE(title), L"Przyłóż kartę");
            return SHStrDupW(title, value);
        }
        return inner->GetStringValue(f, value);
    }
    IFACEMETHODIMP GetBitmapValue(DWORD f, HBITMAP* bitmap) override
    {
        if (f == imageField && bitmap)
        {
            wchar_t path[MAX_PATH];
            ReadSetting(L"TileImage", path, ARRAYSIZE(path), L"");
            HBITMAP image = path[0] ? static_cast<HBITMAP>(LoadImageW(nullptr, path, IMAGE_BITMAP, 0, 0, LR_LOADFROMFILE | LR_CREATEDIBSECTION)) : nullptr;
            if (image) { *bitmap = image; return S_OK; }
        }
        return inner->GetBitmapValue(f, bitmap);
    }
    IFACEMETHODIMP GetCheckboxValue(DWORD f, BOOL* c, LPWSTR* s) override { return inner->GetCheckboxValue(f, c, s); }
    IFACEMETHODIMP GetSubmitButtonValue(DWORD f, DWORD* a) override { return inner->GetSubmitButtonValue(f, a); }
    IFACEMETHODIMP GetComboBoxValueCount(DWORD f, DWORD* c, DWORD* s) override { return inner->GetComboBoxValueCount(f, c, s); }
    IFACEMETHODIMP GetComboBoxValueAt(DWORD f, DWORD i, LPWSTR* s) override { return inner->GetComboBoxValueAt(f, i, s); }
    IFACEMETHODIMP SetStringValue(DWORD f, LPCWSTR s) override { Trace(L"SetStringValue %lu (value not logged)", f); return inner->SetStringValue(f, s); }
    IFACEMETHODIMP SetCheckboxValue(DWORD f, BOOL c) override { return inner->SetCheckboxValue(f, c); }
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD f, DWORD i) override { return inner->SetComboBoxSelectedValue(f, i); }
    IFACEMETHODIMP CommandLinkClicked(DWORD f) override { HRESULT hr = inner->CommandLinkClicked(f); Trace(L"CommandLinkClicked %lu -> %08lX", f, hr); return hr; }
    IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* r, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* cs, LPWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override
    {
        HRESULT hr = inner->GetSerialization(r, cs, text, icon);
        Trace(L"GetSerialization -> %08lX response %d package %lu", hr, r ? *r : -1, cs ? cs->ulAuthenticationPackage : 0);
        // The sign-in result is reported to the provider named here: the wrapper, which passes it on.
        if (SUCCEEDED(hr) && cs && *r == CPGSR_RETURN_CREDENTIAL_FINISHED) cs->clsidCredentialProvider = CLSID_KioskProvider;
        return hr;
    }
    IFACEMETHODIMP ReportResult(NTSTATUS s, NTSTATUS sub, LPWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon) override { Trace(L"ReportResult %08lX/%08lX", s, sub); return inner->ReportResult(s, sub, text, icon); }
    IFACEMETHODIMP GetUserSid(LPWSTR* sid) override { return inner2 ? inner2->GetUserSid(sid) : E_NOTIMPL; }
    IFACEMETHODIMP GetFieldOptions(DWORD f, CREDENTIAL_PROVIDER_CREDENTIAL_FIELD_OPTIONS* o) override { return innerOptions ? innerOptions->GetFieldOptions(f, o) : E_NOTIMPL; }
};

// Text updates pass through: the security key provider shows its prompts ("touch the key", PIN) in these fields.
HRESULT Events::SetFieldString(ICredentialProviderCredential*, DWORD f, LPCWSTR s) { HRESULT hr = outer->SetFieldString(sender, f, s); Trace(L"event SetFieldString %lu \"%s\" -> %08lX", f, s ? s : L"", hr); return hr; }

HRESULT Events::SetFieldBitmap(ICredentialProviderCredential*, DWORD f, HBITMAP b)
{
    auto* wrapper = static_cast<Credential*>(static_cast<ICredentialProviderCredential2*>(sender));
    if (f == wrapper->imageField) return S_OK; // Keep the Kiosk picture.
    return outer->SetFieldBitmap(sender, f, b);
}

// ------------------------------------------------------------------ provider

class Provider final : public ICredentialProvider, public ICredentialProviderSetUserArray
{
    LONG refs = 1;
    ICredentialProvider* inner = nullptr;
    ICredentialProviderSetUserArray* innerUsers = nullptr;
    std::vector<Credential*> credentials;
    DWORD titleField = MAXDWORD, imageField = MAXDWORD;

    void ClearCredentials() { for (auto*& c : credentials) SafeRelease(c); credentials.clear(); }
public:
    Provider() { InterlockedIncrement(&g_objects); }
    ~Provider() { ClearCredentials(); SafeRelease(innerUsers); SafeRelease(inner); InterlockedDecrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == __uuidof(ICredentialProvider)) *ppv = static_cast<ICredentialProvider*>(this);
        else if (riid == __uuidof(ICredentialProviderSetUserArray)) *ppv = static_cast<ICredentialProviderSetUserArray*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs); }
    IFACEMETHODIMP_(ULONG) Release() override { LONG r = InterlockedDecrement(&refs); if (!r) delete this; return r; }

    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD flags) override
    {
        // Sign-in and unlock only; elsewhere (UAC, credential dialogs) Windows' own tiles are used.
        if (cpus != CPUS_LOGON && cpus != CPUS_UNLOCK_WORKSTATION) return E_NOTIMPL;
        if (!inner)
        {
            wchar_t text[64];
            CLSID clsid;
            ReadSetting(L"InnerProvider", text, ARRAYSIZE(text), DefaultInner);
            if (FAILED(CLSIDFromString(text, &clsid)) || clsid == CLSID_KioskProvider) return E_NOTIMPL;
            if (FAILED(CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&inner)))) return E_NOTIMPL;
            inner->QueryInterface(IID_PPV_ARGS(&innerUsers));
        }
        HRESULT hr = inner->SetUsageScenario(cpus, flags);
        Trace(L"SetUsageScenario %d flags %lu -> %08lX", cpus, flags, hr);
        if (FAILED(hr)) return hr;
        // Find the fields to brand: the tile picture and the first large text (the tile title).
        titleField = imageField = MAXDWORD;
        DWORD count = 0;
        if (SUCCEEDED(inner->GetFieldDescriptorCount(&count)))
            for (DWORD i = 0; i < count; i++)
            {
                CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* d = nullptr;
                if (FAILED(inner->GetFieldDescriptorAt(i, &d)) || !d) continue;
                if (d->cpft == CPFT_TILE_IMAGE && imageField == MAXDWORD) imageField = i;
                if (d->cpft == CPFT_LARGE_TEXT && titleField == MAXDWORD) titleField = i;
                CoTaskMemFree(d->pszLabel);
                CoTaskMemFree(d);
            }
        return hr;
    }
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* cs) override { return inner ? inner->SetSerialization(cs) : E_UNEXPECTED; }
    IFACEMETHODIMP Advise(ICredentialProviderEvents* e, UINT_PTR context) override { return inner ? inner->Advise(e, context) : E_UNEXPECTED; }
    IFACEMETHODIMP UnAdvise() override { return inner ? inner->UnAdvise() : E_UNEXPECTED; }
    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* count) override { return inner ? inner->GetFieldDescriptorCount(count) : E_UNEXPECTED; }
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD i, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** d) override
    {
        if (!inner) return E_UNEXPECTED;
        HRESULT hr = inner->GetFieldDescriptorAt(i, d);
        // The picture's label names the tile under "Sign-in options".
        if (SUCCEEDED(hr) && d && *d && (i == imageField || i == titleField))
        {
            wchar_t title[256];
            ReadSetting(L"Title", title, ARRAYSIZE(title), L"Przyłóż kartę");
            LPWSTR label = nullptr;
            if (SUCCEEDED(SHStrDupW(title, &label))) { CoTaskMemFree((*d)->pszLabel); (*d)->pszLabel = label; }
        }
        return hr;
    }
    IFACEMETHODIMP GetCredentialCount(DWORD* count, DWORD* def, BOOL* autoLogon) override
    {
        if (!inner) return E_UNEXPECTED;
        HRESULT hr = inner->GetCredentialCount(count, def, autoLogon);
        Trace(L"GetCredentialCount -> %08lX count %lu default %ld auto %d", hr, count ? *count : 0, def ? (long)*def : -2, autoLogon ? *autoLogon : -1);
        return hr;
    }
    IFACEMETHODIMP GetCredentialAt(DWORD i, ICredentialProviderCredential** result) override
    {
        if (!inner || !result) return E_UNEXPECTED;
        *result = nullptr;
        ICredentialProviderCredential* wrapped = nullptr;
        HRESULT hr = inner->GetCredentialAt(i, &wrapped);
        if (FAILED(hr)) return hr;
        if (credentials.size() <= i) credentials.resize(i + 1, nullptr);
        // The wrapped provider may hand out new credentials after it reports a change.
        if (!credentials[i] || credentials[i]->Inner() != wrapped)
        {
            Trace(L"GetCredentialAt %lu: new credential", i);
            SafeRelease(credentials[i]);
            credentials[i] = new (std::nothrow) Credential(wrapped, titleField, imageField);
        }
        wrapped->Release();
        if (!credentials[i]) return E_OUTOFMEMORY;
        return credentials[i]->QueryInterface(IID_PPV_ARGS(result));
    }
    IFACEMETHODIMP SetUserArray(ICredentialProviderUserArray* users) override
    {
        DWORD n = 0;
        if (users) users->GetCount(&n);
        HRESULT hr = innerUsers ? innerUsers->SetUserArray(users) : S_OK;
        Trace(L"SetUserArray users %lu -> %08lX", n, hr);
        return hr;
    }
};

// ------------------------------------------------------------------ COM plumbing

class Factory final : public IClassFactory
{
    LONG refs = 1;
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid != IID_IUnknown && riid != IID_IClassFactory) return E_NOINTERFACE;
        *ppv = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs); }
    IFACEMETHODIMP_(ULONG) Release() override { LONG r = InterlockedDecrement(&refs); if (!r) delete this; return r; }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* provider = new (std::nothrow) Provider();
        if (!provider) return E_OUTOFMEMORY;
        HRESULT hr = provider->QueryInterface(riid, ppv);
        provider->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&g_locks); else InterlockedDecrement(&g_locks); return S_OK; }
};

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_KioskProvider) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow() { return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE; }

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}
