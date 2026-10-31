using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FidoRun
{
    [ComImport, Guid("00000001-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
    }

    internal static class SystemProvider
    {
        private static IntPtr module;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetClassObject(ref Guid clsid, ref Guid iid, out IntPtr factory);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        public static ICredentialProvider Create(Action<string> log)
        {
            // Load only the OS DLL. Do not honor per-user COM registration overrides.
            string path = Path.Combine(Environment.SystemDirectory, "fidocredprov.dll");
            if (module == IntPtr.Zero) module = LoadLibraryExW(path, IntPtr.Zero, 0x800);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Loading the system fidocredprov.dll failed.");
            IntPtr export = GetProcAddress(module, "DllGetClassObject");
            if (export == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "DllGetClassObject was not found.");
            log("Loaded system provider: " + path);
            GetClassObject getFactory = (GetClassObject)Marshal.GetDelegateForFunctionPointer(export, typeof(GetClassObject));
            Guid clsid = WindowsLogon.FidoProvider;
            Guid factoryId = typeof(IClassFactory).GUID;
            IntPtr rawFactory;
            int status = getFactory(ref clsid, ref factoryId, out rawFactory);
            log("DllGetClassObject: " + Status.Hex(status));
            Status.Check(status, "DllGetClassObject");
            if (rawFactory == IntPtr.Zero) throw new InvalidOperationException("No class factory was returned.");
            IClassFactory factory;
            try { factory = (IClassFactory)Marshal.GetObjectForIUnknown(rawFactory); }
            finally { Marshal.Release(rawFactory); }
            try
            {
                Guid providerId = typeof(ICredentialProvider).GUID;
                IntPtr rawProvider;
                status = factory.CreateInstance(IntPtr.Zero, ref providerId, out rawProvider);
                log("IClassFactory.CreateInstance: " + Status.Hex(status));
                Status.Check(status, "IClassFactory.CreateInstance");
                if (rawProvider == IntPtr.Zero) throw new InvalidOperationException("No provider instance was returned.");
                try { return (ICredentialProvider)Marshal.GetObjectForIUnknown(rawProvider); }
                finally { Marshal.Release(rawProvider); }
            }
            finally { Marshal.ReleaseComObject(factory); }
            // The module remains loaded for this short-lived process. Do not unload
            // an OS COM server while it could still own asynchronous worker threads.
        }
    }

    internal sealed class ProviderHost
    {
        private readonly Options options;
        private readonly TraceLog log;
        private readonly HostForm view;
        private readonly IntPtr owner;
        private Thread thread;
        private Control dispatcher;
        private ICredentialProvider provider;
        private ICredentialProviderWithDisplayState displayProvider;
        private ICredentialProviderCredential credential;
        private IConnectableCredentialProviderCredential connectable;
        private HostCallbacks callbacks;
        private HostCallbacks credentialCallbacks;
        private readonly EmptyUserArray userArray = new EmptyUserArray();
        private readonly List<FieldDescriptor> descriptors = new List<FieldDescriptor>();
        private readonly Dictionary<uint, string> labels = new Dictionary<uint, string>();
        private readonly SubmissionConsent submissionConsent = new SubmissionConsent();
        private bool completionQueued;
        private DateTime serializationReadyUtc;
        private uint count;
        private bool providerAdvised, credentialAdvised, credentialReady, busy, deferredRefresh, lsaAttempted;
        private bool initializing = true, selecting, serializationRequested;
        private int credentialGeneration;
        private uint lastDisplayState = UInt32.MaxValue;
        private volatile bool hostVisible = true;
        private readonly HashSet<Guid> callbackQueries = new HashSet<Guid>();
        private int refreshQueued;
        private volatile bool cancelled, stopping;
        public bool Cancelled { get { return cancelled || stopping; } }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr reserved, uint mode);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();

        public ProviderHost(Options options, TraceLog log, HostForm view, IntPtr owner)
        { this.options = options; this.log = log; this.view = view; this.owner = owner; }

        public void Start()
        {
            thread = new Thread(delegate()
            {
                int apartment = CoInitializeEx(IntPtr.Zero, 2);
                if (apartment < 0) { log.Write("CoInitializeEx failed: " + Status.Hex(apartment)); return; }
                try
                {
                    using (Control control = new Control())
                    {
                        IntPtr unused = control.Handle;
                        dispatcher = control;
                        control.BeginInvoke(new Action(delegate { if (stopping) Application.ExitThread(); else Run(Initialize); }));
                        Application.Run();
                        Cleanup();
                    }
                }
                catch (Exception error) { Fail(error); }
                finally { dispatcher = null; CoUninitialize(); }
            });
            thread.IsBackground = true; thread.Name = "FidoRun provider STA";
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }

        private void Post(Action action)
        {
            Control current = dispatcher;
            if (stopping || current == null || current.IsDisposed) return;
            try { current.BeginInvoke(new Action(delegate { if (!stopping) Run(action); })); }
            catch (InvalidOperationException) { }
        }
        private void Run(Action action)
        {
            Program.BeginOperation();
            try { action(); }
            catch (Exception error) { Fail(error); }
            finally { Program.EndOperation(); }
        }
        private void Fail(Exception error)
        {
            submissionConsent.Clear();
            credentialReady = false;
            view.AllowSubmit(false);
            Win32Exception windowsError = error as Win32Exception;
            log.Write("Failure: " + error.GetType().Name + "; HRESULT=" + Status.Hex(error.HResult) +
                (windowsError == null ? "" : "; Win32=" + windowsError.NativeErrorCode) + "; " + error.Message);
            view.StatusText(error.Message); view.Busy(false);
            if (lsaAttempted) view.RestartAfterFailure("Logowanie nie powiodło się.");
            Program.Result = 2;
        }

        private void Initialize()
        {
            if (Cancelled) return;
            callbacks = new HostCallbacks(this, owner);
            VerifyCallbackInterfaces();
            provider = SystemProvider.Create(log.Write);
            int scenario = options.Scenario == "logon" ? 1 : 4;
            uint flags = scenario == 4 ? 0x200U : 0U;
            int status = provider.SetUsageScenario(scenario, flags);
            log.Write("SetUsageScenario: " + Status.Hex(status) + "; Scenario=" + scenario + "; Flags=0x" + flags.ToString("X4"));
            // ERROR_ACCESS_DISABLED_BY_POLICY: security-key sign-in is not turned on for this PC; say so at once instead of a silent timeout.
            if (status == unchecked((int)0x800704EC) && options.Broker)
                KioskBroker.Failed(KioskBroker.Unsupported + "Logowanie kluczem bezpieczeństwa (FIDO2) jest wyłączone na tym komputerze. Włącz zasadę „Use security keys for sign-in” (Intune) albo UseSecurityKeyForSignin = 1.");
            else if (status != 0 && options.Broker)
                KioskBroker.Failed(KioskBroker.Unsupported + "Windows na tym komputerze nie uruchamia logowania kartą (SetUsageScenario " + Status.Hex(status) + ").");
            Status.Check(status, "SetUsageScenario");
            if (options.UserArray == "empty")
            {
                ICredentialProviderSetUserArray setUsers = provider as ICredentialProviderSetUserArray;
                if (setUsers != null)
                {
                    status = setUsers.SetUserArray(userArray);
                    log.Write("SetUserArray(empty, OtherUser flags=3): " + Status.Hex(status));
                    Status.Check(status, "SetUserArray");
                }
                else log.Write("ICredentialProviderSetUserArray is not exposed.");
            }
            // Advise can retain callbacks before a later native startup step fails.
            // UnAdvise must therefore also run after a partially failed Advise.
            providerAdvised = true;
            status = provider.Advise(callbacks, UIntPtr.Zero);
            log.Write("Provider.Advise: " + Status.Hex(status));
            if (status == NativeCtapProbe.RpcAccessDenied)
            {
                log.Write("Provider startup returned RPC_E_ACCESS_DENIED before enumeration. Key authentication and LSA were not attempted.");
                log.Write("Run a separate process: FidoRun.exe --mode diagnose. This isolates native CTAP RPC from managed COM callbacks.");
            }
            Status.Check(status, "Provider.Advise");
            if (options.Scenario == "logon" && options.DisplayMode != "none")
            {
                displayProvider = provider as ICredentialProviderWithDisplayState;
                log.Write("ICredentialProviderWithDisplayState=" + (displayProvider != null));
            }
            uint fieldCount;
            status = provider.GetFieldDescriptorCount(out fieldCount);
            log.Write("GetFieldDescriptorCount: " + Status.Hex(status) + "; Count=" + fieldCount);
            Status.Check(status, "GetFieldDescriptorCount");
            if (fieldCount > 128) throw new InvalidOperationException("Unexpected field count.");
            for (uint index = 0; index < fieldCount; index++)
            {
                IntPtr pointer = IntPtr.Zero;
                try
                {
                    status = provider.GetFieldDescriptorAt(index, out pointer);
                    Status.Check(status, "GetFieldDescriptorAt(" + index + ")");
                    if (pointer == IntPtr.Zero) throw new InvalidOperationException("A field descriptor was null.");
                    FieldDescriptor descriptor = (FieldDescriptor)Marshal.PtrToStructure(pointer, typeof(FieldDescriptor));
                    labels[descriptor.Id] = descriptor.Label == IntPtr.Zero ? "" : Marshal.PtrToStringUni(descriptor.Label);
                    if (descriptor.Label != IntPtr.Zero) Marshal.FreeCoTaskMem(descriptor.Label);
                    descriptor.Label = IntPtr.Zero;
                    descriptors.Add(descriptor);
                    log.Write("Field descriptor: ID=" + descriptor.Id + "; Type=" + descriptor.Type);
                }
                finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
            }
            initializing = false;
            deferredRefresh = false;
            Refresh();
        }

        public void SetHostVisible(bool visible)
        {
            hostVisible = visible;
            Post(delegate
            {
                if (!initializing && !selecting && !busy) ApplyDisplayState(false);
                QueueNativeCompletion();
            });
        }

        private void ApplyDisplayState(bool closing)
        {
            if (displayProvider == null || options.DisplayMode == "none") return;
            uint flags = DisplayState.ForHost(options.DisplayMode, !closing && hostVisible);
            if (lastDisplayState == flags) return;
            int result = displayProvider.SetDisplayState(flags);
            log.Write("SetDisplayState: " + Status.Hex(result) + "; Flags=0x" + flags.ToString("X8"));
            Status.Check(result, "SetDisplayState");
            lastDisplayState = flags;
        }

        public void RequestRefresh()
        {
            if (Interlocked.Exchange(ref refreshQueued, 1) == 0)
                Post(delegate { Interlocked.Exchange(ref refreshQueued, 0); Refresh(); });
        }
        private void Refresh()
        {
            if (provider == null || stopping || lsaAttempted) return;
            if (busy || initializing || selecting) { deferredRefresh = true; return; }
            uint selected;
            bool automatic;
            int status = provider.GetCredentialCount(out count, out selected, out automatic);
            log.Write("GetCredentialCount: " + Status.Hex(status) + "; Count=" + count + "; Default=" + selected + "; AutoLogon=" + automatic);
            Status.Check(status, "GetCredentialCount");
            log.Write("ProviderEnumeration: CredentialTiles=" + count + "; ConnectedKeyCount=NotMeasured");
            if (count > 32) throw new InvalidOperationException("Unexpected credential count.");
            view.ShowTiles(count);
            if (count == 0)
            {
                ReleaseCredential();
                view.StatusText("Logowanie kartą jest niedostępne na tym komputerze. Szczegóły są w logu.");
                log.Write("Outcome=NoCredentials; this host does not reproduce every LogonUI service.");
                return;
            }
            SelectCore(selected < count ? selected : 0);
        }
        public void Select(uint index) { Post(delegate { if (!busy) SelectCore(index); }); }
        private void SelectCore(uint index)
        {
            if (index >= count) throw new ArgumentOutOfRangeException("index");
            if (selecting) { deferredRefresh = true; return; }
            selecting = true;
            view.Busy(true);
            try
            {
                ReleaseCredential();
                int generation = credentialGeneration;
                view.BeginFields(generation);
                credentialCallbacks = new HostCallbacks(this, owner, generation);
                int status = provider.GetCredentialAt(index, out credential);
                log.Write("GetCredentialAt(" + index + "): " + Status.Hex(status));
                Status.Check(status, "GetCredentialAt");
                if (credential == null) throw new InvalidOperationException("No credential object was returned.");
                credentialAdvised = true;
                status = credential.Advise(credentialCallbacks);
                log.Write("Credential.Advise: " + Status.Hex(status));
                Status.Check(status, "Credential.Advise");
                bool automatic;
                status = credential.SetSelected(out automatic);
                log.Write("SetSelected: " + Status.Hex(status) + "; AutoLogon=" + automatic);
                Status.Check(status, "SetSelected");
                ApplyDisplayState(false);
                connectable = credential as IConnectableCredentialProviderCredential;
                log.Write("IConnectableCredentialProviderCredential=" + (connectable != null));
                List<FieldData> values = ReadFields();
                credentialReady = true;
                view.ShowFields(generation, values);
                view.AllowSubmit(true);
                int visible = 0;
                foreach (FieldData field in values) if (FieldData.IsVisible(field.State)) visible++;
                log.Write("CredentialReady=True; Meaning=InterfacesInitialized; VisibleFields=" + visible + "; AuthenticationReady=NotEstablished");
                view.StatusText(options.Mode == "inspect" ? "Inspection only. Authentication is disabled." :
                    (visible == 0 ? "Przyłóż kartę do czytnika lub włóż klucz zabezpieczeń." : "Wpisz PIN karty i naciśnij Enter."));
            }
            finally
            {
                selecting = false;
                view.Busy(false);
                ApplyDisplayState(false);
                if (deferredRefresh && !initializing && !lsaAttempted) { deferredRefresh = false; RequestRefresh(); }
            }
        }

        private void VerifyCallbackInterfaces()
        {
            IntPtr unknown = Marshal.GetIUnknownForObject(callbacks);
            try
            {
                foreach (Type type in new[] { typeof(ICredentialProviderCredentialEvents), typeof(ICredentialProviderCredentialEvents2),
                    typeof(ICredentialProviderCredentialEvents3), typeof(ICredentialProviderCredentialEvents4), typeof(ICredentialProviderBioEvents) })
                {
                    Guid iid = type.GUID;
                    IntPtr pointer = IntPtr.Zero;
                    try
                    {
                        int status = Marshal.QueryInterface(unknown, ref iid, out pointer);
                        log.Write("Callback interface check: " + type.Name + "; " + Status.Hex(status));
                        Status.Check(status, "Callback interface " + type.Name);
                        if (pointer == IntPtr.Zero) throw new InvalidOperationException("A callback interface pointer was null.");
                    }
                    finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
                }
            }
            finally { Marshal.Release(unknown); }
        }

        internal void LogCallbackQuery(Guid iid)
        {
            lock (callbackQueries)
            {
                if (callbackQueries.Count >= 128 || !callbackQueries.Add(iid)) return;
                log.Write("Callback.QueryInterface: IID=" + iid);
            }
        }

        internal bool IsCurrentCredential(int generation)
        { return generation != 0 && generation == Volatile.Read(ref credentialGeneration) && !stopping; }

        internal void UpdateField(int generation, uint id, string kind, string text, int value)
        {
            if (!IsCurrentCredential(generation)) return;
            // Values of text/PIN fields are never logged.
            if (kind == "state" || kind == "interactive")
                log.Write("Callback.FieldUpdate: Generation=" + generation + "; ID=" + id + "; Kind=" + kind + "; Value=" + value);
            view.UpdateField(generation, id, kind, text, value);
            if (kind == "text")
                Post(delegate
                {
                    if (!IsCurrentCredential(generation)) return;
                    foreach (FieldDescriptor descriptor in descriptors)
                        if (descriptor.Id == id && FieldData.IsDisplayText(descriptor.Type))
                        {
                            log.Write("NativeDisplayText: ID=" + id + "; Type=" + descriptor.Type + "; Text=" + TraceLog.DisplayText(text));
                            break;
                        }
                });
        }

        internal void NativeStatusMessage(int generation, string message)
        {
            if (generation != 0 && !IsCurrentCredential(generation)) return;
            string normalized = (message ?? "").ToLowerInvariant();
            if (HostForm.IsRejectedPin(message))
            {
                submissionConsent.Clear();
                serializationRequested = false;
                view.Busy(false);
                message = "Błędny PIN. Spróbuj ponownie.";
            }
            log.Write("NativeStatusMessage: " + TraceLog.DisplayText(message));
            if (options.Broker) { KioskBroker.ProviderListening(); KioskBroker.ProviderMessage(message); }
            view.StatusText(message);
        }

        internal int NativeBioFeedback(int generation, UIntPtr context, int visualState,
            string label, string accessibility, int priority, bool clear)
        {
            if (stopping || (generation != 0 && !IsCurrentCredential(generation))) return Status.False;
            // Provider.Advise uses context zero. A notification cannot authorize
            // serialization, change identity, submit a PIN, or launch a process.
            if (context != UIntPtr.Zero) return Status.InvalidArgument;
            if (clear)
            {
                log.Write("NativeBioFeedback: Cleared=True");
                view.ProviderFeedback("");
                return Status.Ok;
            }
            string display = TraceLog.DisplayText(String.IsNullOrEmpty(label) ? accessibility : label);
            if (HostForm.IsRejectedPin(display))
            {
                submissionConsent.Clear();
                serializationRequested = false;
                view.Busy(false);
                display = "Błędny PIN. Spróbuj ponownie.";
            }
            log.Write("NativeBioFeedback: VisualState=" + visualState + "; Priority=" + priority + "; Text=" + display);
            if (options.Broker) KioskBroker.ProviderListening();
            if (!String.IsNullOrEmpty(accessibility) && !String.Equals(label, accessibility, StringComparison.Ordinal))
                log.Write("NativeBioFeedbackAccessibility: " + TraceLog.DisplayText(accessibility));
            if (options.Broker) KioskBroker.ProviderMessage(display);
            view.ProviderFeedback(display);
            return Status.Ok;
        }

        internal void NativeSerializationRequested(int generation)
        {
            // Post out of the native callback. Only a previously submitted,
            // unfinished user request for this exact tile may continue automatically.
            if (!IsCurrentCredential(generation)) return;
            DateTime received = DateTime.UtcNow;
            log.Write("Callback.RequestSerialization: Generation=" + generation + "; NativeResponseReady=True");
            Post(delegate
            {
                if (!IsCurrentCredential(generation)) return;
                serializationReadyUtc = received;
                serializationRequested = true;
                view.ProviderFeedback("");
                view.SerializationAvailable(generation);
                if (credentialReady && !busy && !lsaAttempted && !Cancelled)
                    view.StatusText(submissionConsent.CanContinue(generation) ?
                        "Karta odpowiedziała. Trwa logowanie do Windows..." :
                        "Karta odpowiedziała. Naciśnij „Zaloguj”, aby dokończyć.");
                QueueNativeCompletion();
            });
        }

        private void QueueNativeCompletion()
        {
            int generation = credentialGeneration;
            if (completionQueued || !submissionConsent.CanContinue(generation) || !serializationRequested ||
                busy || selecting || initializing || !credentialReady || lsaAttempted || Cancelled || !hostVisible) return;
            completionQueued = true;
            Post(delegate
            {
                completionQueued = false;
                if (!IsCurrentCredential(generation) || !submissionConsent.CanContinue(generation) ||
                    !serializationRequested || busy || !credentialReady || lsaAttempted || Cancelled || !hostVisible) return;
                log.Write("NativeCompletion: ExistingUserRequest=True; Generation=" + generation + "; NoAdditionalUserClick=True");
                SubmitCore();
            });
        }

        internal void NativeSelectionRequested(int generation)
        {
            // This callback object belongs to the current tile, already selected by
            // SelectCore. Calling SetSelected recursively would restart its workflow.
            if (IsCurrentCredential(generation)) log.Write("Callback.RequestSelection: received for the current tile.");
        }

        private string ReadString(uint id)
        {
            IntPtr value = IntPtr.Zero;
            try
            {
                int status = credential.GetStringValue(id, out value);
                if (status < 0) { log.Write("GetStringValue field " + id + ": " + Status.Hex(status)); return ""; }
                return value == IntPtr.Zero ? "" : Marshal.PtrToStringUni(value);
            }
            finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
        }
        private List<FieldData> ReadFields()
        {
            List<FieldData> result = new List<FieldData>();
            foreach (FieldDescriptor descriptor in descriptors)
            {
                int state, interactive;
                int status = credential.GetFieldState(descriptor.Id, out state, out interactive);
                log.Write("GetFieldState: ID=" + descriptor.Id + "; Result=" + Status.Hex(status) + "; State=" + state + "; Interactive=" + interactive);
                if (status < 0) continue;
                FieldData field = new FieldData { Id = descriptor.Id, Type = descriptor.Type,
                    State = state, Interactive = interactive, Label = labels[descriptor.Id] };
                // Never retrieve or log password/PIN field contents.
                if (field.Type == 1 || field.Type == 2 || field.Type == 3 || field.Type == 4 || field.Type == 9)
                    field.Value = ReadString(field.Id);
                if (field.Type == 7)
                {
                    IntPtr label = IntPtr.Zero;
                    try
                    {
                        bool value;
                        status = credential.GetCheckboxValue(field.Id, out value, out label);
                        if (status >= 0) { field.Checked = value; field.Value = label == IntPtr.Zero ? field.Label : Marshal.PtrToStringUni(label); }
                    }
                    finally { if (label != IntPtr.Zero) Marshal.FreeCoTaskMem(label); }
                }
                if (field.Type == 8)
                {
                    uint itemCount, selected;
                    status = credential.GetComboBoxValueCount(field.Id, out itemCount, out selected);
                    if (status >= 0)
                    {
                        if (itemCount > 256) throw new InvalidOperationException("Unexpected combo box item count.");
                        field.Selected = selected;
                        for (uint index = 0; index < itemCount; index++)
                        {
                            IntPtr value = IntPtr.Zero;
                            try
                            {
                                status = credential.GetComboBoxValueAt(field.Id, index, out value);
                                field.Items.Add(status >= 0 && value != IntPtr.Zero ? Marshal.PtrToStringUni(value) : "");
                            }
                            finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
                        }
                    }
                }
                result.Add(field);
            }
            return result;
        }

        public void StringValue(int generation, uint id, string value)
        { Post(delegate { if (IsCurrentCredential(generation) && !busy && credentialReady && credential != null) Status.Check(credential.SetStringValue(id, value), "SetStringValue(field " + id + ")"); }); }
        public void CheckboxValue(int generation, uint id, bool value)
        { Post(delegate { if (IsCurrentCredential(generation) && !busy && credentialReady && credential != null) Status.Check(credential.SetCheckboxValue(id, value), "SetCheckboxValue"); }); }
        public void ComboValue(int generation, uint id, uint value)
        { Post(delegate { if (IsCurrentCredential(generation) && !busy && credentialReady && credential != null) Status.Check(credential.SetComboBoxSelectedValue(id, value), "SetComboBoxSelectedValue"); }); }
        public void Command(int generation, uint id)
        {
            if (options.Mode == "inspect") return;
            cancelled = false;
            Post(delegate
            {
                if (!IsCurrentCredential(generation) || busy || !credentialReady || credential == null || Cancelled) return;
                log.Write("CommandLinkClicked: ID=" + id);
                Status.Check(credential.CommandLinkClicked(id), "CommandLinkClicked");
            });
        }
        public void Cancel()
        {
            cancelled = true;
            submissionConsent.Clear();
            log.Write("Cancellation requested.");
            view.StatusText("Anulowanie… chwileczkę.");
        }
        public void Submit(int generation = 0)
        {
            if (options.Mode == "inspect") return;
            cancelled = false;
            Post(delegate
            {
                if ((generation != 0 && !IsCurrentCredential(generation)) || !credentialReady || busy || lsaAttempted || Cancelled) return;
                submissionConsent.Begin(credentialGeneration);
                SubmitCore();
            });
        }
        private void SubmitCore()
        {
            if (!credentialReady || !credentialAdvised || credential == null)
            {
                log.Write("Outcome=SubmitBlocked; Reason=CredentialInitializationIncomplete; NoSerialization=True; NoLsaLogon=True");
                view.AllowSubmit(false);
                return;
            }
            if (busy || lsaAttempted || Cancelled || !hostVisible) return;
            bool hasInput = false;
            foreach (FieldDescriptor descriptor in descriptors)
            {
                int state, interactive;
                int fieldStatus = credential.GetFieldState(descriptor.Id, out state, out interactive);
                if (fieldStatus >= 0 && FieldData.CanSubmit(descriptor.Type, state, interactive)) { hasInput = true; break; }
            }
            if (!serializationRequested && !hasInput)
            {
                log.Write("Outcome=SubmitBlocked; Reason=NoVisibleInputOrNativeSerializationRequest; NoSerialization=True; NoLsaLogon=True");
                view.StatusText("Przygotowuję logowanie kartą…");
                return;
            }
            busy = true; view.Busy(true);
            bool continuingNative = submissionConsent.CanContinue(credentialGeneration);
            bool nativePending = false;
            CredentialSerialization serialization = new CredentialSerialization();
            IntPtr statusText = IntPtr.Zero;
            try
            {
                if (connectable != null && !continuingNative)
                {
                    view.StatusText("Łączenie z kartą...");
                    log.Write("Connect: starting");
                    int connected = connectable.Connect(credentialCallbacks);
                    log.Write("Connect: " + Status.Hex(connected));
                    Status.Check(connected, "Connect");
                    if (connected != Status.Ok) throw new OperationCanceledException("The provider did not complete Connect with S_OK.");
                }
                if (Cancelled) throw new OperationCanceledException("FIDO operation was cancelled.");
                int response, icon;
                if (serializationRequested && serializationReadyUtc != default(DateTime))
                    log.Write("SerializationRequestDelayMs=" + Math.Max(0, (DateTime.UtcNow - serializationReadyUtc).TotalMilliseconds).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                serializationRequested = false;
                serializationReadyUtc = default(DateTime);
                view.SerializationUnavailable(credentialGeneration);
                view.StatusText("Sprawdzanie karty...");
                log.Write("GetSerialization: starting");
                int status = credential.GetSerialization(out response, out serialization, out statusText, out icon);
                log.Write("GetSerialization: " + Status.Hex(status) + "; Response=" + response + "; Bytes=" + serialization.Length + "; Package=" + serialization.AuthenticationPackage + "; Provider=" + serialization.Provider);
                if (statusText != IntPtr.Zero) NativeStatusMessage(credentialGeneration, Marshal.PtrToStringUni(statusText));
                Status.Check(status, "GetSerialization");
                if (Cancelled) throw new OperationCanceledException("FIDO operation was cancelled.");
                if (status == Status.Ok && response == 0)
                {
                    nativePending = true;
                    submissionConsent.Wait(credentialGeneration);
                    log.Write("Outcome=CredentialPending; Response=CPGSR_NO_CREDENTIAL_NOT_FINISHED; NoLsaLogon=True; WaitingForNativeCallback=True");
                    if (statusText == IntPtr.Zero)
                        view.StatusText("Dotknij karty lub klucza, jeśli Windows o to prosi, i poczekaj.");
                    return;
                }
                if (status != Status.Ok || response != 2)
                {
                    log.Write("Outcome=NoFinishedCredential; no Windows logon attempt was made.");
                    if (statusText == IntPtr.Zero) view.StatusText("Logowanie kartą nie zostało dokończone. Spróbuj ponownie.");
                    return;
                }
                if (serialization.Length == 0 || serialization.Buffer == IntPtr.Zero)
                {
                    serializationRequested = false;
                    view.SerializationUnavailable(credentialGeneration);
                    log.Write("Outcome=EmptyCredential; NoLsaLogon=True; the native provider returned no authentication material.");
                    view.StatusText("Przyłóż kartę i wpisz PIN, a potem spróbuj ponownie.");
                    return;
                }
                if (options.AccountPicker) AccountCredentialFilter.RequireBoundSelection();
                WindowsLogon.ValidateSerialization(serialization);
                submissionConsent.Clear();
                lsaAttempted = true;
                view.AllowSubmit(false);
                view.ProviderFeedback("");
                view.StatusText("Logowanie profilu…");
                using (LogonOutcome outcome = WindowsLogon.Authenticate(serialization, log.Write))
                {
                    if (options.Scenario == "logon") ReportResult(outcome.Status, outcome.SubStatus);
                    if (!outcome.Success)
                    {
                        Program.Result = 3;
                        log.Write("Outcome=WindowsLogonFailed");
                        view.StatusText("Windows odrzucił logowanie (" + Status.Hex(outcome.Status) + " / " + Status.Hex(outcome.SubStatus) + "). Spróbuj ponownie.");
                        view.RestartAfterFailure("Windows odrzucił logowanie (" + Status.Hex(outcome.Status) + ").");
                        return;
                    }
                    Program.Result = 0;
                    view.StatusText("Zalogowano: " + outcome.Name);
                    log.Write("Outcome=WindowsTokenCreated");
                    string targetSid = options.ResolveTargetSid(outcome.Sid);
                    if ((options.ExpectedSid != null || options.Broker) && targetSid == null)
                    {
                        Program.Result = 5;
                        log.Write("Outcome=TargetSidMismatch; Expected=" + options.ExpectedSid);
                        view.StatusText("Uwierzytelniono " + outcome.Name + ", ale ta karta należy do innego konta niż wymagane.");
                        view.RestartAfterFailure("Ta karta nie jest dozwolona na tym komputerze.");
                        return;
                    }
                    if (targetSid != null) log.Write("TargetBoundToAuthenticatedToken=True; SID=" + targetSid);
                    if (Cancelled)
                    {
                        Program.Result = 2;
                        log.Write("Outcome=CancelledAfterLogon; no application was launched.");
                        return;
                    }
                    if (options.Broker)
                    {
                        string brokerAccount = options.AccountPicker && !String.IsNullOrWhiteSpace(AccountCredentialFilter.SelectedAccountLabel)
                            ? AccountCredentialFilter.SelectedAccountLabel : outcome.Name;
                        KioskBroker.SignedIn(outcome, targetSid, brokerAccount);
                        view.BrokerSignedIn(brokerAccount);
                    }
                }
            }
            finally
            {
                if (statusText != IntPtr.Zero) Marshal.FreeCoTaskMem(statusText);
                WindowsLogon.ClearSerialization(ref serialization);
                // The provider owns its asynchronous workflow after response 0.
                // Clear our UI copy, but do not mutate or disconnect that workflow.
                if (!nativePending)
                {
                    submissionConsent.Clear();
                    foreach (FieldDescriptor field in descriptors)
                        if (field.Type == 5 && credential != null)
                            try { credential.SetStringValue(field.Id, ""); } catch { log.Write("PIN field cleanup callback was unavailable."); }
                }
                view.ClearSecrets();
                if (connectable != null && !nativePending)
                {
                    try { log.Write("Disconnect: " + Status.Hex(connectable.Disconnect())); }
                    catch (Exception error) { log.Write("Disconnect failed: " + error.GetType().Name); }
                }
                busy = false; view.Busy(nativePending);
                ApplyDisplayState(false);
                if (deferredRefresh && !lsaAttempted) { deferredRefresh = false; Refresh(); }
                QueueNativeCompletion();
            }
        }
        private void ReportResult(int status, int subStatus)
        {
            IntPtr text = IntPtr.Zero;
            try
            {
                int icon;
                int result = credential.ReportResult(status, subStatus, out text, out icon);
                log.Write("ReportResult: " + Status.Hex(result));
                if (text != IntPtr.Zero) view.StatusText(Marshal.PtrToStringUni(text));
            }
            catch (Exception error) { log.Write("ReportResult callback failed: " + error.GetType().Name + "; HRESULT=" + Status.Hex(error.HResult)); }
            finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
        }
        private void ReleaseCredential()
        {
            Interlocked.Increment(ref credentialGeneration);
            submissionConsent.Clear();
            serializationReadyUtc = default(DateTime);
            serializationRequested = false;
            credentialReady = false;
            view.AllowSubmit(false);
            connectable = null;
            if (credential == null) return;
            try { credential.SetDeselected(); } catch { }
            if (credentialAdvised) { try { credential.UnAdvise(); } catch { } }
            credentialAdvised = false;
            Marshal.ReleaseComObject(credential); credential = null;
            credentialCallbacks = null;
        }
        private void Cleanup()
        {
            try { ApplyDisplayState(true); }
            catch (Exception error) { log.Write("Display-state cleanup failed: " + Status.Hex(error.HResult)); }
            ReleaseCredential();
            displayProvider = null;
            if (provider != null)
            {
                if (providerAdvised) { try { provider.UnAdvise(); } catch { } }
                providerAdvised = false;
                Marshal.ReleaseComObject(provider); provider = null;
            }
            GC.KeepAlive(callbacks); GC.KeepAlive(userArray);
        }
        public void Stop()
        {
            cancelled = true; stopping = true;
            Control current = dispatcher;
            if (current != null && !current.IsDisposed)
            {
                try { current.BeginInvoke(new Action(delegate { if (!busy) Application.ExitThread(); })); }
                catch (InvalidOperationException) { }
            }
            if (thread != null) thread.Join(1500);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class HostCallbacks : ICredentialProviderEvents, ICredentialProviderBioEvents,
        ICredentialProviderCredentialEvents, ICredentialProviderCredentialEvents2,
        ICredentialProviderCredentialEvents3, ICredentialProviderCredentialEvents4,
        IQueryContinueWithStatus, IQueryContinue, ICustomQueryInterface
    {
        private readonly ProviderHost host;
        private readonly IntPtr owner;
        private readonly int generation;
        internal HostCallbacks(ProviderHost host, IntPtr owner, int generation = 0)
        { this.host = host; this.owner = owner; this.generation = generation; }
        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr pointer)
        {
            pointer = IntPtr.Zero;
            host.LogCallbackQuery(iid);
            return CustomQueryInterfaceResult.NotHandled;
        }
        public int CredentialsChanged(UIntPtr context) { host.RequestRefresh(); return Status.Ok; }
        public int BioFeedbackVisualStateChanged(UIntPtr context, int visualState, string label, string accessibility)
        { return host.NativeBioFeedback(generation, context, visualState, label, accessibility, 1, false); }
        public int BioFeedbackVisualStateChangedWithPriority(UIntPtr context, int visualState, string label, string accessibility, int priority)
        { return host.NativeBioFeedback(generation, context, visualState, label, accessibility, priority, false); }
        public int CancelBioFeedbackVisualState(UIntPtr context)
        { return host.NativeBioFeedback(generation, context, 0, null, null, 0, true); }
        public int QueryContinue() { return host.Cancelled || (generation != 0 && !host.IsCurrentCredential(generation)) ? Status.False : Status.Ok; }
        public int SetStatusMessage(string message) { host.NativeStatusMessage(generation, message); return Status.Ok; }
        public int OnCreatingWindow(out IntPtr window) { window = owner; return Status.Ok; }
        public int SetFieldState(ICredentialProviderCredential credential, uint id, int value) { host.UpdateField(generation, id, "state", null, value); return Status.Ok; }
        public int SetFieldInteractiveState(ICredentialProviderCredential credential, uint id, int value) { host.UpdateField(generation, id, "interactive", null, value); return Status.Ok; }
        public int SetFieldString(ICredentialProviderCredential credential, uint id, string value) { KioskBroker.ProviderMessage(value); host.UpdateField(generation, id, "text", value, 0); return Status.Ok; }
        public int SetFieldCheckbox(ICredentialProviderCredential credential, uint id, bool value, string label) { host.UpdateField(generation, id, "checkbox", label, value ? 1 : 0); return Status.Ok; }
        public int SetFieldBitmap(ICredentialProviderCredential credential, uint id, IntPtr bitmap) { return Status.Ok; }
        public int SetFieldBitmapBuffer(ICredentialProviderCredential credential, uint id, uint bytes, IntPtr buffer)
        {
            // Cosmetic only: the UI uses a fixed FIDO label and never retains this borrowed image buffer.
            if (bytes > 1048576 || (bytes != 0 && buffer == IntPtr.Zero)) return Status.InvalidArgument;
            return Status.Ok;
        }
        public int RequestSerialization() { host.NativeSerializationRequested(generation); return Status.Ok; }
        public int RequestSelection() { host.NativeSelectionRequested(generation); return Status.Ok; }
        public int SetFieldComboBoxSelectedItem(ICredentialProviderCredential credential, uint id, uint index) { host.UpdateField(generation, id, "combo-select", null, unchecked((int)index)); return Status.Ok; }
        public int DeleteFieldComboBoxItem(ICredentialProviderCredential credential, uint id, uint index) { host.UpdateField(generation, id, "combo-delete", null, unchecked((int)index)); return Status.Ok; }
        public int AppendFieldComboBoxItem(ICredentialProviderCredential credential, uint id, string value) { host.UpdateField(generation, id, "combo-append", value, 0); return Status.Ok; }
        public int SetFieldSubmitButton(ICredentialProviderCredential credential, uint id, uint adjacentTo) { return Status.Ok; }
        public int BeginFieldUpdates() { return Status.Ok; }
        public int EndFieldUpdates() { return Status.Ok; }
        public int SetFieldOptions(ICredentialProviderCredential credential, uint id, uint options) { host.UpdateField(generation, id, "options", null, unchecked((int)options)); return Status.Ok; }
    }
}
