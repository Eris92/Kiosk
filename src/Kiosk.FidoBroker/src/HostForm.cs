using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FidoRun
{
    internal sealed class FieldData
    {
        public uint Id;
        public int Type, State, Interactive;
        public string Label, Value;
        public bool Checked;
        public uint Selected;
        public readonly List<string> Items = new List<string>();
        public static bool IsVisible(int state) { return state == 1 || state == 3; }
        // Only native display fields can be included in diagnostic text logs.
        // Edit/PIN fields (types 4/5) and their values are always excluded.
        public static bool IsDisplayText(int type) { return type == 1 || type == 2 || type == 3 || type == 9; }
        public static bool CanSubmit(int type, int state, int interactive)
        {
            if (!IsVisible(state) || interactive == 1 || interactive == 2) return false;
            // Command links (type 3), such as Retry, perform their own action;
            // they must not enable the host's unrelated serialization button.
            return type == 4 || type == 5 || type == 7 || type == 8 || type == 9;
        }
    }

    internal sealed class HostForm : Form
    {
        private readonly Options options;
        private readonly TraceLog log;
        private readonly Label providerStatus = new Label();
        private readonly FlowLayoutPanel fields = new FlowLayoutPanel();
        private readonly TextBox trace = new TextBox();
        private Control providerFeedback;
        private readonly bool pinScreen;
        private string signedInAccount;
        private Timer restartTimer;
        private readonly Label state = new Label();
        private readonly Button submit = new Button();
        private readonly Dictionary<uint, Control> inputs = new Dictionary<uint, Control>();
        private readonly Dictionary<uint, Control> containers = new Dictionary<uint, Control>();
        private readonly Dictionary<uint, FieldData> fieldData = new Dictionary<uint, FieldData>();
        private readonly List<Action> pendingFieldUpdates = new List<Action>();
        private int fieldGeneration;
        private bool preparingFields, serializationAvailable, fieldUpdatesFailed;
        private ProviderHost host;
        private bool rendering, closing, canSubmit, working;
        private AccountCredentialFilter accountFilter;

        private bool Hidden { get { return options.Broker && options.PinFromClient; } }
        private bool Embedded { get { return options != null && options.Embedded; } }
        // Inside the Kiosk: its scale and colours (surface, text, muted, accent).
        private float scale = 1f;
        private Color statusColor = SystemColors.ControlText;
        private Color kioskSurface, kioskText, kioskMuted, kioskAccent;
        private Timer fitTimer;
        private TableLayoutPanel pinCard;
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                if (options != null && options.Broker && options.PinFromClient) parameters.ExStyle |= 0x20 | 0x80; // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
                if (Embedded)
                {
                    // Created right inside the Kiosk's window (also whenever WinForms re-applies the styles).
                    parameters.Style = (parameters.Style & ~unchecked((int)0x80000000) & ~0x00C00000) | 0x40000000 | 0x02000000; // -POPUP -CAPTION +CHILD +CLIPCHILDREN
                    parameters.ExStyle &= ~0x00040000;                    // -WS_EX_APPWINDOW
                    parameters.Parent = options.EmbedParent;
                }
                return parameters;
            }
        }

        // ---- Kiosk broker with the PIN from the Kiosk (any thread) ----

        // The PIN box of the current tile, when it takes input and a submission is possible.
        private TextBox ReadyPinBox()
        {
            if (closing || working || !canSubmit || preparingFields || fieldUpdatesFailed || !fields.Enabled) return null;
            foreach (KeyValuePair<uint, Control> pair in inputs)
            {
                TextBox box = pair.Value as TextBox;
                Control container;
                if (box == null || !box.UseSystemPasswordChar || box.ReadOnly || !box.Enabled) continue;
                if (!containers.TryGetValue(pair.Key, out container) || !container.Visible) continue;
                return box;
            }
            return null;
        }

        // Types the PIN into the native field and submits it, like the person would on the PIN screen.
        internal bool TryEnterPin(string pin)
        {
            if (closing || IsDisposed || !IsHandleCreated || host == null) return false;
            try
            {
                return (bool)Invoke(new Func<bool>(delegate
                {
                    TextBox box = ReadyPinBox();
                    if (box == null || host == null) return false;
                    Activate();   // The Kiosk allowed it; Windows finishes the card sign-in for the foreground window.
                    box.Text = pin;   // TextChanged hands the value to the provider.
                    host.Submit();
                    log.Write("PinFromClient=Submitted");
                    return true;
                }));
            }
            catch (InvalidOperationException) { return false; }
        }

        // Windows talks to an NFC card only for the window in the foreground (a retry clicked in a visible window
        // worked, the same retry from a background window never reached the card).
        private void TakeForeground(string reason)
        {
            // Inside the Kiosk only while its sign-in place is on screen: a hidden place (a page or Szybka pomoc open,
            // a person working) must not take the keyboard from what the person is typing into.
            if (Embedded && !IsWindowVisible(options.EmbedParent))
            {
                log.Write("PinFromClient=ForegroundSkipped; Reason=" + reason + "; PlaceHidden=True");
                return;
            }
            BringToFront();
            Activate();
            SetForegroundWindow(Handle);
            log.Write("PinFromClient=Foreground; Reason=" + reason + "; IsForeground=" + (GetForegroundWindow() == Handle));
        }

        internal void EnsureForeground(string reason)
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try { Invoke(new Action(delegate { if (GetForegroundWindow() != Handle) TakeForeground(reason); })); }
            catch (InvalidOperationException) { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        private int autoRetries;
        private DateTime lastAutoRetry;

        // The first card exchange with a card already lying on the reader often ends in "Nie można zakończyć
        // logowania" with a "try again" link; nobody sees this window, so it is clicked here (a few times at most).
        internal bool TryAutoRetry()
        {
            if (closing || IsDisposed || !IsHandleCreated || host == null) return false;
            try
            {
                return (bool)Invoke(new Func<bool>(delegate
                {
                    if (working || host == null || autoRetries >= 10 || (DateTime.UtcNow - lastAutoRetry).TotalSeconds < 4) return false;
                    // Only after Windows reported the failed exchange, never for other links of the tile.
                    string shown = (state.Text ?? "") + " " + (providerFeedback == null ? "" : providerFeedback.Text);
                    if (shown.IndexOf("ponownie", StringComparison.OrdinalIgnoreCase) < 0 && shown.IndexOf("again", StringComparison.OrdinalIgnoreCase) < 0) return false;
                    foreach (KeyValuePair<uint, FieldData> pair in fieldData)
                    {
                        Control input, container;
                        if (pair.Value.Type != 3 || !inputs.TryGetValue(pair.Key, out input) || !input.Enabled) continue;
                        if (!containers.TryGetValue(pair.Key, out container) || !container.Visible) continue;
                        autoRetries++;
                        lastAutoRetry = DateTime.UtcNow;
                        TakeForeground("AutoRetry");
                        log.Write("PinFromClient=AutoRetry; Field=" + pair.Key + "; Attempt=" + autoRetries);
                        host.Command(fieldGeneration, pair.Key);
                        return true;
                    }
                    return false;
                }));
            }
            catch (InvalidOperationException) { return false; }
        }

        // After a submission: the reason when the tile is waiting for a PIN again (wrong PIN, card removed), else null.
        internal string WaitingForPinAgain()
        {
            if (closing || IsDisposed || !IsHandleCreated) return null;
            try
            {
                return (string)Invoke(new Func<string>(delegate
                {
                    TextBox box = ReadyPinBox();
                    if (box == null || box.TextLength > 0) return null;
                    string feedback = providerFeedback == null ? "" : providerFeedback.Text;
                    return feedback.Length > 0 ? feedback : state.Text;
                }));
            }
            catch (InvalidOperationException) { return null; }
        }

        public HostForm(Options options, TraceLog log)
        {
            this.options = options; this.log = log;
            pinScreen = options.PinScreen;
            // Inside the Kiosk's card: the Kiosk's own sizes at the Kiosk's DPI (smaller only when it does not fit).
            if (Embedded) scale = options.EmbedDpi / 96f;
            if (pinScreen) BuildPinScreen(); else BuildDiagnosticWindow();
            if (Hidden)
            {
                // The Kiosk shows its own PIN screen; this window only hosts the native provider. It is fully
                // transparent and lets clicks through, but stays visible and in the foreground while Windows talks to
                // the card (Windows' security key sign-in refuses a window in the background).
                TopMost = true;
                ShowInTaskbar = false;
                WindowState = FormWindowState.Normal;
                StartPosition = FormStartPosition.Manual;
                Bounds = Screen.PrimaryScreen.Bounds;
                // FIDORUN_VISIBLE=1 (diagnostics): the native tile stays visible over the Kiosk.
                Opacity = Environment.GetEnvironmentVariable("FIDORUN_VISIBLE") == "1" ? 1 : 0.01;
                KioskBroker.View = this;
                Shown += delegate { KioskBroker.ViewHandle = Handle; TakeForeground("Shown"); };
            }
            if (Embedded)
            {
                // The Kiosk's sign-in place: a child window of the Kiosk, filling the place it was given. ShowInTaskbar
                // stays on: off, WinForms makes a hidden helper window this window's owner, which for a child window
                // means its parent, and the sign-in disappears from the Kiosk.
                TopMost = false;
                WindowState = FormWindowState.Normal;
                StartPosition = FormStartPosition.Manual;
                // Not a top-level form: WinForms then never sets an owner on it, which for a child window replaces its
                // parent with the desktop (it showed for a moment at the top of the screen before the timer put it back).
                TopLevel = false;
                KioskBroker.View = this;
            }

            // Wrap the two status labels to the window width instead of clipping/overflowing.
            Resize += delegate
            {
                if (Embedded) return; // Fixed layout owns the label bounds, including their height.
                int w = Embedded ? TextWidth : pinScreen ? FieldWidth : ClientSize.Width - 28;
                if (w < 80) return;
                providerStatus.MaximumSize = new Size(w, 0);
                state.MaximumSize = new Size(w, 0);
            };

            // Enter (for example right after typing the PIN) triggers the login button.
            AcceptButton = submit;
            if (!pinScreen) log.Line += AddLog;
            Shown += delegate
            {
                if (!pinScreen)
                {
                    // Never let the window exceed the screen, so the docked buttons stay reachable
                    // on small or scaled kiosk displays.
                    Rectangle work = Screen.FromControl(this).WorkingArea;
                    int w = Math.Min(Width, work.Width), h = Math.Min(Height, work.Height);
                    if (w != Width || h != Height) Size = new Size(w, h);
                    Location = new Point(work.X + Math.Max(0, (work.Width - Width) / 2),
                                         work.Y + Math.Max(0, (work.Height - Height) / 2));
                }
                if (options.AccountPicker)
                {
                    try
                    {
                        log.Write("AccountChoice=Requested; SingleNativePin=True; PickerOnlyForMultipleAccounts=True");
                        accountFilter = AccountCredentialFilter.InstallAutomatic(ChooseKeyAccount,log.Write);
                    }
                    catch (Exception error)
                    {
                        log.Write("AccountChoice=CancelledOrFailed; " + error.GetType().Name + "; HRESULT=" + Status.Hex(error.HResult));
                        state.Text = "Wybór konta nie został zakończony. Logowanie Windows nie zostało rozpoczęte. " + error.Message;
                        // The Kiosk waits for an answer: tell it at once instead of letting it wait for its time limit.
                        if (options.Broker) KioskBroker.Failed("Logowanie kartą nie może wystartować na tej wersji Windows (" + error.Message + ").");
                        return;
                    }
                }
                host = new ProviderHost(options, log, this, Handle);
                host.SetHostVisible(Visible && WindowState != FormWindowState.Minimized);
                host.Start();
            };
            VisibleChanged += delegate { if (host != null) host.SetHostVisible(Visible && WindowState != FormWindowState.Minimized); };
            Resize += delegate { if (host != null) host.SetHostVisible(Visible && WindowState != FormWindowState.Minimized); };
            FormClosing += delegate
            {
                if (fitTimer != null) { fitTimer.Dispose(); fitTimer = null; }
                if (restartTimer != null) { restartTimer.Dispose(); restartTimer = null; }
                closing = true;
                if (!pinScreen) log.Line -= AddLog;
                foreach (Control input in inputs.Values) if (input is TextBox) ((TextBox)input).Clear();
                if (host != null) host.Stop();
                if (accountFilter != null) accountFilter.Dispose();
            };
        }

        private int FieldWidth { get { return (int)((Embedded ? 360 : pinScreen ? 560 : 650) * scale); } }
        private int TextWidth { get { return (int)(540 * scale); } }

        // A font of the Kiosk's size: points at the Kiosk's DPI, in pixels so nothing rescales it a second time.
        private Font KioskFont(float points, FontStyle style = FontStyle.Regular)
        { return new Font("Segoe UI", points * scale * 96f / 72f, style, GraphicsUnit.Pixel); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!Embedded) return;
            KioskBroker.ViewHandle = Handle;
            // A child of the Kiosk's window from now on: it moves, hides and shows with the Kiosk.
            int style = GetWindowLong(Handle, -16);                       // GWL_STYLE
            SetWindowLong(Handle, -16, (style & ~unchecked((int)0x80000000) & ~0x00C00000) | 0x40000000 | 0x02000000); // -POPUP -CAPTION +CHILD +CLIPCHILDREN
            if (SetParent(Handle, options.EmbedParent) == IntPtr.Zero)
                log.Write("Embed=Failed; Error=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            else log.Write("Embed=Ok; Parent=" + options.EmbedParent.ToInt64() + "; Dpi=" + options.EmbedDpi);
            FitToParent(false);
            fitTimer = new Timer { Interval = 300 };
            fitTimer.Tick += delegate { FitToParent(true); };
            fitTimer.Start();
        }

        // Fills the Kiosk's sign-in place; the Kiosk gone means nobody can sign in here any more.
        private void FitToParent(bool repair)
        {
            if (!IsWindow(options.EmbedParent))
            {
                if (fitTimer != null) fitTimer.Stop();
                KioskBroker.Failed("Okno Kiosku zostało zamknięte.");
                Close();
                return;
            }
            if (repair && GetParent(Handle) != options.EmbedParent)
            {
                log.Write("Embed=Reattached; Was=" + GetParent(Handle).ToInt64());
                SetParent(Handle, options.EmbedParent);
            }
            if (repair && !Visible && signedInAccount == null && !closing) Show();   // Hidden only once signed in.
            Rect area;
            if (!GetClientRect(options.EmbedParent, out area)) return;
            if (Left != 0 || Top != 0 || Width != area.Right || Height != area.Bottom)
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, area.Right, area.Bottom, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
            ShrinkToFit(area.Right, area.Bottom);
        }

        // The whole sign-in stays visible in the Kiosk's card: when it does not fit, everything gets smaller together.
        private void ShrinkToFit(int width, int height)
        {
            if (pinCard == null || width <= 0 || height <= 0) return;
            for (int round = 0; round < 3; round++)
            {
                Size needed = pinCard.GetPreferredSize(Size.Empty) + pinCard.Margin.Size;
                float ratio = Math.Min((float)width / needed.Width, (float)height / needed.Height);
                if (ratio >= 1f) return;
                ratio *= 0.97f;
                scale *= ratio;
                SuspendLayout();
                ScaleContent(this, ratio);
                ResumeLayout(true);
                log.Write("Embed=Shrunk; Ratio=" + ratio.ToString("0.00") + "; Needed=" + needed.Width + "x" + needed.Height + "; Place=" + width + "x" + height);
            }
        }

        private static void ScaleContent(Control control, float ratio)
        {
            control.Font = new Font(control.Font.FontFamily, Math.Max(6f, control.Font.Size * ratio), control.Font.Style, control.Font.Unit);
            if (control.MaximumSize.Width > 0) control.MaximumSize = new Size((int)(control.MaximumSize.Width * ratio), 0);
            if (control.MinimumSize.Width > 0) control.MinimumSize = new Size((int)(control.MinimumSize.Width * ratio), 0);
            if (control is TextBox || control is ComboBox) control.Width = (int)(control.Width * ratio);
            else if (!control.AutoSize && (control is Button || control is Label)) control.Size = new Size((int)(control.Width * ratio), (int)(control.Height * ratio));
            foreach (Control child in control.Controls) ScaleContent(child, ratio);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect area);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        private static Color Rgb(int value) { return Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255); }

        private void BuildDiagnosticWindow()
        {
            Text = "FidoRun 0.1.29 - native FIDO provider host";
            Font = new Font("Segoe UI", 10f);
            // The app is DPI-unaware (see app.manifest), so Windows scales this whole window up
            // on a high-DPI kiosk. These are the logical sizes before that scaling.
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = new Size(480, 520);
            ClientSize = new Size(620, 600);
            StartPosition = FormStartPosition.CenterScreen;

            // One table owns the whole window; cells never overlap and auto-size rows grow to
            // fit wrapped text, so the layout holds at any size or DPI.
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 0 provider status
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 1 input fields (PIN)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));   // 2 key notification
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 3 buttons
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 4 status line
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));   // 5 diagnostic log

            providerStatus.AutoSize = true; providerStatus.Margin = new Padding(0, 0, 0, 6);
            providerStatus.Font = new Font(Font, FontStyle.Bold);
            providerStatus.Text = "Sprawdzanie dostawcy FIDO...";
            root.Controls.Add(providerStatus, 0, 0);

            fields.Dock = DockStyle.Fill; fields.FlowDirection = FlowDirection.TopDown; fields.WrapContents = false; fields.AutoScroll = true; fields.Enabled = false;
            root.Controls.Add(fields, 0, 1);

            TextBox feedback = new TextBox();
            feedback.Dock = DockStyle.Fill; feedback.Multiline = true;
            feedback.ReadOnly = true; feedback.TabStop = false;
            feedback.ScrollBars = ScrollBars.Vertical;
            feedback.BackColor = SystemColors.Info; feedback.ForeColor = SystemColors.InfoText;
            feedback.AccessibleName = "Powiadomienie klucza zabezpieczeń";
            feedback.Text = "Oczekiwanie na klucz zabezpieczeń.";
            providerFeedback = feedback;
            root.Controls.Add(providerFeedback, 0, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 2) };
            submit.Text = "Zaloguj kluczem FIDO"; submit.AutoSize = true; submit.Enabled = false; submit.Margin = new Padding(0, 0, 8, 4);
            submit.Click += delegate { if (host != null) host.Submit(); };
            Button cancel = new Button { Text = "Anuluj", AutoSize = true, Margin = new Padding(0, 0, 8, 4) };
            cancel.Click += delegate { if (host != null) host.Cancel(); };
            buttons.Controls.Add(submit);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 3);

            state.AutoSize = true; state.Margin = new Padding(0, 2, 0, 4); state.Text = "Uruchamianie dostawcy FIDO...";
            root.Controls.Add(state, 0, 4);

            trace.Dock = DockStyle.Fill; trace.Multiline = true; trace.ReadOnly = true;
            trace.ScrollBars = ScrollBars.Both; trace.WordWrap = false; trace.Font = new Font("Consolas", 9);
            root.Controls.Add(trace, 0, 5);

            Controls.Add(root);
        }

        // The Kiosk's card sign-in: one card with the native PIN field. Diagnostics stay in the log file.
        private void BuildPinScreen()
        {
            Color accent = Color.FromArgb(0, 95, 184);
            Color surface = Color.White, text = Color.FromArgb(32, 32, 32), muted = Color.FromArgb(80, 80, 80);
            if (Embedded)
            {
                surface = Rgb(options.EmbedColors[0]); text = Rgb(options.EmbedColors[1]);
                muted = Rgb(options.EmbedColors[2]); accent = Rgb(options.EmbedColors[3]);
                statusColor = muted;
                kioskSurface = surface; kioskText = text; kioskMuted = muted; kioskAccent = accent;
            }
            Text = "Logowanie kartą";
            Font = Embedded ? KioskFont(11) : new Font("Segoe UI", 14f * scale);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = Embedded ? FormWindowState.Normal : FormWindowState.Maximized;
            StartPosition = FormStartPosition.Manual;
            BackColor = Embedded ? surface : Color.FromArgb(232, 237, 243);
            ForeColor = text;

            TableLayoutPanel outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            TableLayoutPanel card = new TableLayoutPanel
            {
                ColumnCount = 1, RowCount = 6, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = surface, Padding = Embedded ? new Padding(0) : new Padding(48, 40, 48, 36), Margin = new Padding(16)
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            if (Embedded) card.MinimumSize = new Size(FieldWidth, (int)(360 * scale));
            for (int row = 0; row < 6; row++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label title = new Label { Text = "Zaloguj się kartą", AutoSize = true, Font = new Font("Segoe UI Semibold", 26f * scale), ForeColor = text, Margin = new Padding(0, 0, 0, 8) };
            signInTitle = title;
            card.Controls.Add(title, 0, 0);

            providerStatus.AutoSize = true; providerStatus.Margin = new Padding(0, 0, 0, 16);
            providerStatus.ForeColor = muted;
            providerStatus.MaximumSize = new Size(FieldWidth, 0);
            providerStatus.Text = "Przygotowanie logowania...";
            card.Controls.Add(providerStatus, 0, 1);

            Label feedback = new Label { AutoSize = true, MaximumSize = new Size(FieldWidth, 0), ForeColor = accent, Margin = new Padding(0, 0, 0, 8) };
            feedback.AccessibleName = "Powiadomienie klucza zabezpieczeń";
            feedback.Text = "Przyłóż kartę do czytnika.";
            providerFeedback = feedback;
            card.Controls.Add(providerFeedback, 0, 2);

            fields.AutoSize = true; fields.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            fields.FlowDirection = FlowDirection.TopDown; fields.WrapContents = false; fields.Enabled = false;
            fields.MinimumSize = new Size(FieldWidth, 0); fields.Margin = new Padding(0);
            card.Controls.Add(fields, 0, 3);

            buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 16, 0, 8) };
            // In the Kiosk the buttons do nothing until the card is tapped: shown together with the PIN field.
            if (options.Broker && Embedded) buttons.Visible = false;
            StylePrimary(submit, accent);
            submit.Text = "Zaloguj"; submit.Enabled = false;
            submit.Click += delegate { if (host != null) host.Submit(); };
            Button restart = new Button { Text = "Anuluj", AutoSize = true, FlatStyle = FlatStyle.Flat, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(0, 0, 12, 4) };
            restart.FlatAppearance.BorderColor = Embedded ? muted : Color.FromArgb(180, 180, 180);
            restart.ForeColor = text;
            restart.Click += delegate { KioskBroker.Failed("Anulowano logowanie kartą."); Close(); };
            if (options.Broker)
            {
                // Over the Kiosk's own full-screen window until the person has signed in.
                TopMost = !options.PinFromClient && !Embedded;
                if (!options.PinFromClient && !Embedded) Shown += delegate { Activate(); };
            }
            buttons.Controls.Add(submit);
            buttons.Controls.Add(restart);
            card.Controls.Add(buttons, 0, 4);
            if (Embedded)
            {
                loginProgress = new SmoothProgressPanel { Width = FieldWidth, Height = (int)(52 * scale), Visible = false };
                System.Diagnostics.Stopwatch animationClock = System.Diagnostics.Stopwatch.StartNew();
                loginProgress.Paint += delegate(object sender, PaintEventArgs e)
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    float diameter = (float)(30 * scale);
                    using (Pen pen = new Pen(accent, (float)(3 * scale)))
                    {
                        pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        e.Graphics.DrawArc(pen, (loginProgress.Width - diameter) / 2, (loginProgress.Height - diameter) / 2, diameter, diameter, (float)(animationClock.Elapsed.TotalMilliseconds * 0.24 % 360), 270);
                    }
                };
                Timer animation = new Timer { Interval = 16 };
                animation.Tick += delegate { loginProgress.Invalidate(); };
                loginProgress.VisibleChanged += delegate { animation.Enabled = loginProgress.Visible; };
                loginProgress.Disposed += delegate { animation.Dispose(); };
                card.RowCount = 7;
                card.Controls.Add(loginProgress, 0, 6);
            }

            state.AutoSize = true; state.Margin = new Padding(0, 4, 0, 0);
            state.ForeColor = Embedded ? muted : Color.FromArgb(90, 90, 90);
            state.Font = new Font("Segoe UI", 12f * scale);
            state.MaximumSize = new Size(FieldWidth, 0);
            state.Text = "Uruchamianie...";
            card.Controls.Add(state, 0, 5);

            pinCard = card;
            if (Embedded) ApplyKioskLook(title, buttons, restart);
            outer.Controls.Add(card, 1, 1);
            Controls.Add(outer);
            if (Embedded)
            {
                Panel content = new Panel { Dock = DockStyle.Fill, BackColor = surface };
                Control[] parts = { title, providerStatus, providerFeedback, fields, buttons, state, loginProgress };
                foreach (Control part in parts) if (part != null) { part.Parent.Controls.Remove(part); content.Controls.Add(part); }
                Controls.Remove(outer);
                outer.Dispose();
                pinCard = null; // The old auto-sized card no longer participates in sizing.
                Controls.Add(content);
                Action arrange = delegate
                {
                    int width = Math.Min(FieldWidth, Math.Max(0, content.Width - (int)(24 * scale)));
                    int left = (content.Width - width) / 2;
                    title.AutoSize = false; title.MaximumSize = Size.Empty;
                    title.SetBounds(0, (int)(36 * scale), content.Width, (int)(70 * scale));
                    providerStatus.AutoSize = false; providerStatus.MaximumSize = Size.Empty;
                    providerStatus.SetBounds(0, (int)(116 * scale), content.Width, (int)(64 * scale));
                    fields.SetBounds(left, (int)(180 * scale), width, fields.PreferredSize.Height);
                    buttons.SetBounds(left, (int)(260 * scale), width, (int)(46 * scale));
                    if (loginProgress != null) loginProgress.SetBounds(left, (int)(190 * scale), width, (int)(52 * scale));
                };
                content.Resize += delegate { arrange(); };
                arrange();
                title.Text = "Logowanie";
                providerFeedback.Visible = false;
                state.Visible = false;
            }
        }

        // Inside the Kiosk: centred like the Kiosk's own cards, its colours, its buttons.
        private void ApplyKioskLook(Label title, FlowLayoutPanel buttons, Button restart)
        {
            // The same type and sizes as the Kiosk's own PIN card: bold heading, one text size under it, 46 px buttons.
            foreach (Control control in new Control[] { title, providerStatus, providerFeedback, fields, buttons, state })
                control.Anchor = AnchorStyles.Top;
            foreach (Label label in new[] { title, providerStatus, (Label)providerFeedback, state })
            {
                label.TextAlign = ContentAlignment.MiddleCenter;
                label.MaximumSize = new Size(TextWidth, 0);
            }
            title.Font = KioskFont(22, FontStyle.Bold);
            providerStatus.Font = KioskFont(11);
            providerFeedback.Font = KioskFont(11);
            state.Font = KioskFont(10);
            providerStatus.Text = "Przygotowuję logowanie kartą...";
            title.Margin = new Padding(0, 0, 0, (int)(10 * scale));
            providerStatus.Margin = new Padding(0, 0, 0, (int)(6 * scale));
            providerFeedback.Margin = new Padding(0, 0, 0, (int)(6 * scale));
            buttons.Margin = new Padding(0, (int)(16 * scale), 0, 0);
            state.Margin = new Padding(0, (int)(10 * scale), 0, 0);
            submit.ForeColor = OnAccent(kioskAccent);
            restart.BackColor = kioskSurface;
            restart.ForeColor = kioskText;
            restart.FlatAppearance.BorderColor = Blend(kioskSurface, kioskMuted, 0.3f);
            restart.FlatAppearance.MouseOverBackColor = Blend(kioskSurface, kioskMuted, 0.15f);
            // The two buttons side by side exactly as wide as the PIN box.
            int half = (FieldWidth - (int)(12 * scale)) / 2;
            foreach (Button button in new[] { submit, restart })
            {
                button.Font = KioskFont(10, FontStyle.Bold);
                button.Cursor = Cursors.Hand;
                button.AutoSize = false;
                button.Size = new Size(half, (int)(46 * scale));
                button.Padding = Padding.Empty;
            }
            submit.Margin = new Padding(0, 0, FieldWidth - 2 * half, 0);
            restart.Margin = Padding.Empty;
            buttons.WrapContents = false;
            // "Przyłóż kartę" / "Wpisz PIN" repeat the line under the heading; the line under the buttons is for news only.
            state.TextChanged += delegate { state.Visible = false; };
            state.Visible = !IsInstruction(state.Text);
        }

        private static bool IsInstruction(string text)
        {
            return String.IsNullOrEmpty(text) || text.StartsWith("Uruchamianie", StringComparison.Ordinal)
                || text.StartsWith("Przyłóż kartę", StringComparison.Ordinal) || text.StartsWith("Wpisz PIN", StringComparison.Ordinal);
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            return Color.FromArgb((int)(from.R + (to.R - from.R) * amount), (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
        }

        private static Color OnAccent(Color accent)
        {
            double luminance = (0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255;
            return luminance > 0.45 ? Color.FromArgb(16, 24, 39) : Color.White;
        }

        // A native field drawn like the Kiosk: dark PIN box, muted captions, accent links.
        private void StyleField(Control input, int type)
        {
            if (!Embedded) return;
            TextBox box = input as TextBox;
            if (box != null)
            {
                box.BackColor = Color.FromArgb((kioskSurface.R * 2) / 3, (kioskSurface.G * 2) / 3, (kioskSurface.B * 2) / 3);
                box.ForeColor = kioskText;
                box.BorderStyle = BorderStyle.FixedSingle;
                if (box.UseSystemPasswordChar) { box.TextAlign = HorizontalAlignment.Center; box.Font = KioskFont(20); }
                return;
            }
            Button button = input as Button;
            // The native "OK" repeats "Zaloguj", the tile's name repeats the title; the "try again" link is pressed by
            // itself after a failed exchange (ScheduleRetry), the person only taps the key again.
            if (type == 9 || type == 6 || type == 3) { input.Visible = false; return; }
            if (button != null)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = kioskSurface;
                button.ForeColor = kioskAccent;
                button.FlatAppearance.BorderColor = kioskAccent;
                button.Font = KioskFont(10, FontStyle.Bold);
                // A provider link ("Spróbuj ponownie" after an error) as wide and high as the Kiosk's own buttons.
                button.AutoSize = false;
                button.Size = new Size(FieldWidth, (int)(46 * scale));
                button.TextAlign = ContentAlignment.MiddleCenter;
                button.Cursor = Cursors.Hand;
                return;
            }
            input.ForeColor = type == 1 ? kioskText : kioskMuted;
            input.Font = KioskFont(11, type == 1 ? FontStyle.Bold : FontStyle.Regular);
            CentreLabel(input as Label);
        }

        private void CentreLabel(Label label)
        {
            if (label == null) return;
            label.AutoSize = false;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Width = FieldWidth;
            label.Height = TextRenderer.MeasureText(label.Text ?? "", label.Font, new Size(FieldWidth, 0), TextFormatFlags.WordBreak).Height + 4;
        }

        private static void StylePrimary(Button button, Color accent)
        {
            button.AutoSize = true; button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
            button.BackColor = accent; button.ForeColor = Color.White;
            button.Padding = new Padding(16, 6, 16, 6); button.Margin = new Padding(0, 0, 12, 4);
        }

        public void Post(Action action)
        {
            if (closing || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { if (!closing && !IsDisposed) action(); })); }
            catch (InvalidOperationException) { }
        }

        private int ChooseKeyAccount(List<KeyAccount> accounts)
        {
            if(closing||IsDisposed)return -1;
            if(InvokeRequired)return (int)Invoke(new Func<int>(delegate{return ChooseKeyAccount(accounts);}));
            if(Embedded)return ChooseKeyAccountInKiosk(accounts);
            using(var dialog=new Form {Text="Wybierz konto z karty",Width=560,Height=330,TopMost=Hidden||Embedded,
                StartPosition=Hidden||Embedded?FormStartPosition.CenterScreen:FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false})
            {
                var list=new ListBox {Dock=DockStyle.Fill,Font=new Font(Font.FontFamily,12),IntegralHeight=false};
                foreach(var account in accounts)list.Items.Add(account);
                var heading=new Label{Text="Wybierz konto, na którym chcesz się zalogować",Dock=DockStyle.Top,Height=44,Padding=new Padding(10)};
                var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48,FlowDirection=FlowDirection.RightToLeft};
                var ok=new Button{Text="Zaloguj",DialogResult=DialogResult.OK,Enabled=false,AutoSize=true};
                var cancel=new Button{Text="Anuluj",DialogResult=DialogResult.Cancel,AutoSize=true};
                list.SelectedIndexChanged+=delegate{ok.Enabled=list.SelectedIndex>=0;};
                list.DoubleClick+=delegate{if(list.SelectedIndex>=0){dialog.DialogResult=DialogResult.OK;dialog.Close();}};
                buttons.Controls.Add(ok);buttons.Controls.Add(cancel);dialog.Controls.Add(list);dialog.Controls.Add(heading);dialog.Controls.Add(buttons);
                dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                return dialog.ShowDialog(this)==DialogResult.OK ? list.SelectedIndex : -1;
            }
        }
        // Inside the Kiosk: the accounts on the card as the Kiosk's own tiles, over the sign-in card in the same place.
        private int ChooseKeyAccountInKiosk(List<KeyAccount> accounts)
        {
            int chosen = -1;
            bool done = false;
            int width = (int)(440 * scale);
            // A panel over the sign-in in this same window, not a window of its own: nothing opens or moves on the screen.
            using (Panel dialog = new Panel
            {
                Bounds = ClientRectangle, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = kioskSurface, ForeColor = kioskText, Font = KioskFont(11)
            })
            {
                TableLayoutPanel outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, BackColor = kioskSurface };
                outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                outer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                outer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                FlowLayoutPanel content = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = kioskSurface, Margin = Padding.Empty, MaximumSize = new Size(0, Math.Max(100, ClientSize.Height - (int)(24 * scale)))
                };
                Label title = new Label { Text = "Wybierz konto", AutoSize = false, Width = width, Height = KioskFont(22, FontStyle.Bold).Height + (int)(4 * scale), Font = KioskFont(22, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 0, (int)(6 * scale)) };
                Label hint = new Label { Text = "Na karcie jest kilka kont. Wybierz, na które się zalogować.", AutoSize = false, Width = width, Font = KioskFont(11), ForeColor = kioskMuted, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 0, 0, (int)(14 * scale)) };
                // Measured with its own font: a label not yet on the form would measure with the default 9 pt one and get cut.
                hint.Height = TextRenderer.MeasureText(hint.Text, hint.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height + (int)(6 * scale);
                content.Controls.Add(title);
                content.Controls.Add(hint);
                Color tile = Blend(kioskSurface, kioskMuted, 0.08f), line = Blend(kioskSurface, kioskMuted, 0.3f);
                for (int i = 0; i < accounts.Count; i++)
                {
                    int index = i;
                    Button account = new Button
                    {
                        Text = accounts[i].Name, Width = width, Height = (int)(52 * scale), FlatStyle = FlatStyle.Flat, BackColor = tile, ForeColor = kioskText,
                        Font = KioskFont(11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding((int)(14 * scale), 0, 0, 0),
                        Margin = new Padding(0, 0, 0, (int)(8 * scale)), Cursor = Cursors.Hand, AutoEllipsis = true, UseVisualStyleBackColor = false
                    };
                    account.FlatAppearance.BorderColor = line;
                    account.FlatAppearance.MouseOverBackColor = Blend(kioskSurface, kioskAccent, 0.18f);
                    account.FlatAppearance.MouseDownBackColor = Blend(kioskSurface, kioskAccent, 0.3f);
                    account.MouseEnter += delegate { account.FlatAppearance.BorderColor = kioskAccent; };
                    account.MouseLeave += delegate { account.FlatAppearance.BorderColor = line; };
                    account.Click += delegate { chosen = index; done = true; };
                    account.KeyDown += delegate (object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) done = true; };
                    content.Controls.Add(account);
                }
                Button cancel = new Button
                {
                    Text = "Anuluj", Width = width, Height = (int)(46 * scale), FlatStyle = FlatStyle.Flat, BackColor = kioskSurface, ForeColor = kioskText,
                    Font = KioskFont(10, FontStyle.Bold), Margin = new Padding(0, (int)(8 * scale), 0, 0), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
                };
                cancel.Click += delegate { done = true; };
                cancel.KeyDown += delegate (object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) done = true; };
                cancel.FlatAppearance.BorderColor = line;
                cancel.FlatAppearance.MouseOverBackColor = Blend(kioskSurface, kioskMuted, 0.15f);
                content.Controls.Add(cancel);
                if (content.MaximumSize.Height > 0) content.AutoScroll = true;
                outer.Controls.Add(content, 1, 1);
                dialog.Controls.Add(outer);
                Controls.Add(dialog);
                dialog.BringToFront();
                if (content.Controls.Count > 2) content.Controls[2].Focus();
                log.Write("AccountChoice=Shown; Accounts=" + accounts.Count + "; InKiosk=True");
                // The credential provider waits for the answer on this thread: the window keeps working meanwhile.
                try
                {
                    while (!done && !closing && !IsDisposed)
                    {
                        Application.DoEvents();
                        if (!done) System.Threading.Thread.Sleep(15);
                    }
                }
                finally { if (!IsDisposed) Controls.Remove(dialog); }
            }
            FocusPin();
            log.Write("AccountChoice=" + (chosen >= 0 ? "Chosen; Index=" + chosen : "Cancelled") + "; Accounts=" + accounts.Count + "; InKiosk=True");
            return chosen;
        }

        private void AddLog(string line) { Post(delegate { trace.AppendText(line + Environment.NewLine); }); }
        public void StatusText(string message) { Post(delegate { ShowProviderText(state, message); }); }
        public void ProviderFeedback(string message) { Post(delegate { ShowProviderText((Label)providerFeedback, message); }); }

        // Inside the Kiosk: a failed card exchange is said in the Kiosk's words and in red, and the tile listens again by
        // itself (no "OK" link to press). Windows reports it when the key still lies on the reader instead of being tapped.
        private bool problemShown;
        private bool awaitingCardRemoval;
        private bool touchRequested;
        private bool pinRejected;
        private const string WaitingForCard = "Przyłóż klucz do czytnika lub włóż go do USB, a następnie dotknij klucza. Jeśli był już włożony, wyjmij go i włóż ponownie.";
        internal static bool IsRejectedPin(string message)
        {
            string value = (message ?? "").ToLowerInvariant();
            return value.Contains("pin") && (value.Contains("niepopraw") || value.Contains("nieprawid") || value.Contains("błęd") || value.Contains("incorrect") || value.Contains("wrong"));
        }
        private Timer retryTimer;

        private void ShowProviderText(Label label, string message)
        {
            message = message ?? "";
            if (!Embedded) { label.Text = message; return; }
            label = providerStatus;
            if (String.IsNullOrWhiteSpace(message)) message = working ? "Sprawdzanie PIN-u…" : "Przyłóż kartę do czytnika.";
            string lower = message.ToLowerInvariant();
            bool rejectedPin = IsRejectedPin(message);
            if (rejectedPin)
            {
                working = false;
                touchRequested = false;
                pinRejected = true;
                ApplyInteractionState();
                FocusPin();
                return;
            }
            bool failed = lower.Contains("nie można zakończyć logowania") || lower.Contains("couldn't complete") || lower.Contains("can't complete");
            bool unknown = lower.Contains("nieznany") || lower.Contains("unknown");
            bool tapAgain = lower.StartsWith("dotknij") || lower.StartsWith("włóż") || lower.StartsWith("touch") || lower.StartsWith("insert");
            if (working && !tapAgain && !lower.StartsWith("przyłóż")) touchRequested = false;
            if (awaitingCardRemoval && !unknown) return;
            // Windows often sends a generic status immediately after rejecting a PIN.
            // Keep the useful error alongside the same editable PIN field until the
            // person types again, instead of flashing the waiting-for-card screen.
            if (pinRejected && !working && !unknown && !failed) return;
            if (!working && (tapAgain || lower.StartsWith("przyłóż"))) message = WaitingForCard;
            if (working && !failed && !unknown && (tapAgain || lower.StartsWith("przyłóż") || lower.StartsWith("zalogowano")))
            {
                touchRequested = tapAgain || lower.StartsWith("przyłóż");
                label.Text = touchRequested ? "Dotknij klucza, aby dokończyć logowanie." : "Trwa logowanie…";
                label.Visible = true;
                label.ForeColor = kioskAccent;
                return;
            }
            if (failed)
            {
                message = "Wyjmij klucz z czytnika lub USB, włóż go ponownie i dotknij klucza, aby się zalogować.";
                awaitingCardRemoval = true;
                ScheduleRetry();
            }
            // While the red request stands, Windows' own "touch the reader" after the retry does not replace it.
            else if (problemShown && message.Length == 0) return;
            problemShown = failed || unknown || rejectedPin;
            label.ForeColor = problemShown ? ProblemColor() : kioskAccent;
            label.Text = message;
            if (problemShown) label.Visible = true;
            // The other line must not keep an old red text.
            Label other = label == state ? (Label)providerFeedback : state;
            if (problemShown && other != null && other.ForeColor == ProblemColor()) { other.Text = ""; other.ForeColor = other == state ? kioskMuted : kioskAccent; }
        }

        private Color ProblemColor()
        {
            double luminance = (0.2126 * kioskSurface.R + 0.7152 * kioskSurface.G + 0.0722 * kioskSurface.B) / 255;
            return luminance > 0.5 ? Color.FromArgb(190, 30, 45) : Color.FromArgb(255, 107, 107);
        }

        // The hidden "try again" link of the tile, pressed after a moment so a new tap is read.
        private void ScheduleRetry()
        {
            if (retryTimer == null)
            {
                retryTimer = new Timer { Interval = 250 };
                retryTimer.Tick += delegate
                {
                    if (closing || IsDisposed || working || host == null) return;
                    // A timer is not evidence that the person removed their key.
                    // Unknown reader state also must not trigger another FIDO request.
                    if (BrokerPolicy.AnyCardPresent() != false) return;
                    awaitingCardRemoval = false;
                    foreach (KeyValuePair<uint, FieldData> pair in fieldData)
                    {
                        Control input, container;
                        if (pair.Value.Type != 3 || !inputs.TryGetValue(pair.Key, out input) || !input.Enabled) continue;
                        if (!containers.TryGetValue(pair.Key, out container) || !container.Visible) continue;
                        log.Write("Embedded=AutoRetry; Field=" + pair.Key);
                        retryTimer.Stop();
                        host.Command(fieldGeneration, pair.Key);
                        return;
                    }
                };
            }
            retryTimer.Stop();
            retryTimer.Start();
        }
        private FlowLayoutPanel buttons;
        private Label signInTitle;
        private Panel loginProgress;
        private sealed class SmoothProgressPanel : Panel
        {
            internal SmoothProgressPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        }
        private void ApplyInteractionState()
        {
            bool hasInput = false;
            foreach (FieldData field in fieldData.Values)
                if (FieldData.CanSubmit(field.Type, field.State, field.Interactive)) { hasInput = true; break; }
            if (options.Broker && Embedded && buttons != null)
            {
                buttons.Visible = hasInput && !working;
                fields.Visible = !working;
                if (signInTitle != null) signInTitle.Text = "Logowanie";
                providerStatus.Text = awaitingCardRemoval ? "Wyjmij klucz z czytnika lub USB, włóż go ponownie i dotknij klucza, aby się zalogować." : pinRejected && !working ? "Błędny PIN. Spróbuj ponownie." : working ? touchRequested ? "Dotknij klucza, aby dokończyć logowanie." : "Sprawdzanie PIN-u…" : hasInput ? "Wpisz PIN karty, potem dotknij klucza." : WaitingForCard;
                providerStatus.Visible = true;
                providerStatus.ForeColor = awaitingCardRemoval || pinRejected && !working ? ProblemColor() : kioskAccent;
                state.Visible = false;
                if (loginProgress != null) loginProgress.Visible = working;
                if (providerFeedback != null) providerFeedback.Visible = false;
            }
            submit.Enabled = !working && canSubmit && !preparingFields && !fieldUpdatesFailed && (hasInput || serializationAvailable);
            fields.Enabled = !working && canSubmit && !fieldUpdatesFailed;
        }
        public void Busy(bool value) { Post(delegate { if (signedInAccount != null) return; if (value && !working) { touchRequested = false; pinRejected = false; } working = value; ApplyInteractionState(); if (!value) FocusPin(); }); }

        // Diagnostics for "the PIN box takes no keys": focus changes and how many characters arrived, never the PIN.
        // A click on the box takes the keyboard explicitly, also when the Kiosk around this window holds it.
        private void WatchPinBox(TextBox box)
        {
            box.GotFocus += delegate { log.Write("PinField=Focused"); };
            box.LostFocus += delegate { log.Write("PinField=LostFocus"); };
            box.MouseDown += delegate
            {
                bool had = box.Focused;
                if (!had) box.Focus();
                log.Write("PinField=Clicked; HadFocus=" + had + "; Focused=" + box.Focused + "; Enabled=" + box.Enabled + "; ReadOnly=" + box.ReadOnly
                    + "; FormsEnabled=" + fields.Enabled + "; RootIsForeground=" + (GetForegroundWindow() == GetAncestor(Handle, 2)));
            };
            int typed = 0;
            box.KeyPress += delegate { if (++typed <= 12) log.Write("PinField=KeyPress; Count=" + typed); };
        }

        // On the PIN screen the visible PIN box always holds the cursor, so the user just types.
        private void FocusPin()
        {
            if (!pinScreen || closing || !fields.Enabled || !fields.Visible) return;
            foreach (KeyValuePair<uint, Control> pair in inputs)
            {
                TextBox box = pair.Value as TextBox;
                Control container;
                if (box == null || !box.UseSystemPasswordChar || box.ReadOnly || !box.Enabled) continue;
                if (!containers.TryGetValue(pair.Key, out container) || !container.Visible) continue;
                if (!box.Focused) box.Focus();
                return;
            }
        }
        public void AllowSubmit(bool value) { Post(delegate { canSubmit = value && options.Mode != "inspect"; ApplyInteractionState(); }); }

        public void BeginFields(int generation)
        {
            Post(delegate
            {
                fieldGeneration = generation;
                preparingFields = true;
                fieldUpdatesFailed = false;
                serializationAvailable = false;
                canSubmit = false;
                pendingFieldUpdates.Clear();
                // Keep the PIN control in the same position while Windows replaces its
                // credential fields. Clear its value and disable it until ShowFields
                // swaps the new controls in one UI turn; removing it here exposed an
                // empty card between an incorrect PIN and the retry field.
                rendering = true;
                try
                {
                    foreach (Control input in inputs.Values)
                        if (input is TextBox box && box.UseSystemPasswordChar) box.Clear();
                }
                finally { rendering = false; }
                ApplyInteractionState();
            });
        }

        public void SerializationAvailable(int generation)
        { Post(delegate { if (generation == fieldGeneration) { serializationAvailable = true; ApplyInteractionState(); } }); }
        public void SerializationUnavailable(int generation)
        { Post(delegate { if (generation == fieldGeneration) { serializationAvailable = false; ApplyInteractionState(); } }); }

        public void ShowTiles(uint count)
        {
            Post(delegate
            {
                rendering = true;
                try
                {
                    // There is always a single system FIDO provider, so the former tile
                    // picker is replaced by a status line. Zero means it could not load.
                    providerStatus.Text = count == 0
                        ? "Logowanie kartą jest niedostępne na tym komputerze (brak dostawcy FIDO w Windows)."
                        : Embedded ? "Przyłóż kartę do czytnika, potem wpisz PIN." : "Dostawca FIDO gotowy. Włóż klucz i podaj PIN.";
                    providerStatus.ForeColor = count == 0 ? Color.Firebrick : statusColor;
                    // Enumeration does not establish successful Advise/SetSelected.
                    canSubmit = false;
                    ApplyInteractionState();
                    if (count == 0) { ClearFieldControls(); inputs.Clear(); containers.Clear(); fieldData.Clear(); pendingFieldUpdates.Clear(); }
                }
                finally { rendering = false; }
            });
        }

        // Kiosk broker: signed in; the window goes away and the process keeps the token for the Kiosk.
        public void BrokerSignedIn(string account)
        {
            Post(delegate
            {
                signedInAccount = account;
                TopMost = false;
                Hide();
                if (Embedded)
                {
                    // Out of the Kiosk altogether: a child window from another program shares the Kiosk's keyboard
                    // and mouse input, and would keep it waiting on this program.
                    if (fitTimer != null) fitTimer.Stop();
                    SetParent(Handle, new IntPtr(-3));   // HWND_MESSAGE
                }
                log.Write("PinScreen=SignedIn; Broker=True; WindowHidden=True");
            });
        }

        // After a failed Windows logon the native tile cannot be reused: the Kiosk shows the reason and offers to
        // try again with a fresh broker.
        public void RestartAfterFailure(string message)
        {
            if (!options.Broker) return;
            KioskBroker.Failed(message);
            Post(delegate
            {
                state.Text = message;
                restartTimer = new Timer { Interval = 4000 };
                restartTimer.Tick += delegate { restartTimer.Stop(); Close(); };
                restartTimer.Start();
            });
        }

        public void ShowFields(int generation, List<FieldData> values)
        {
            Post(delegate
            {
                if (generation != fieldGeneration) return;
                rendering = true;
                fields.SuspendLayout();
                try
                {
                    ClearFieldControls(); inputs.Clear(); containers.Clear(); fieldData.Clear();
                    foreach (FieldData field in values)
                    {
                        Control input;
                        string caption = String.IsNullOrEmpty(field.Value) ? field.Label : field.Value;
                        switch (field.Type)
                        {
                            case 1: case 2:
                                input = new Label { Text = caption ?? "", AutoSize = true, MaximumSize = new Size(FieldWidth, 0) };
                                if (field.Type == 1) input.Font = new Font(Font, FontStyle.Bold);
                                break;
                            case 3:
                                Button action = new Button { Text = caption ?? "Provider action", AutoSize = true };
                                action.Click += delegate { host.Command(generation, field.Id); };
                                input = action; break;
                            case 4: case 5:
                                TextBox text = new TextBox { Width = FieldWidth, Text = field.Type == 5 ? "" : (field.Value ?? ""), UseSystemPasswordChar = field.Type == 5 };
                                if (pinScreen && field.Type == 5) text.Font = new Font(Font.FontFamily, 22f * scale);
                                text.TextChanged += delegate { if (!rendering && !closing) { if (pinRejected && text.Text.Length > 0) { pinRejected = false; ApplyInteractionState(); } host.StringValue(generation, field.Id, text.Text); } };
                                text.ReadOnly = field.Interactive == 1;
                                if (field.Type == 5) WatchPinBox(text);
                                input = text; break;
                            case 6:
                                input = new Label { Text = "FIDO security key", AutoSize = true }; break;
                            case 7:
                                CheckBox check = new CheckBox { Text = caption ?? "", Checked = field.Checked, AutoSize = true };
                                check.CheckedChanged += delegate { if (!rendering) host.CheckboxValue(generation, field.Id, check.Checked); };
                                input = check; break;
                            case 8:
                                ComboBox combo = new ComboBox { Width = FieldWidth, DropDownStyle = ComboBoxStyle.DropDownList };
                                foreach (string item in field.Items) combo.Items.Add(item);
                                if (field.Selected < field.Items.Count) combo.SelectedIndex = (int)field.Selected;
                                combo.SelectedIndexChanged += delegate { if (!rendering && combo.SelectedIndex >= 0) host.ComboValue(generation, field.Id, (uint)combo.SelectedIndex); };
                                input = combo; break;
                            case 9:
                                Button nativeSubmit = new Button { Text = caption ?? "Continue", AutoSize = true };
                                nativeSubmit.Click += delegate { host.Submit(generation); };
                                input = nativeSubmit; break;
                            default: continue;
                        }
                        StyleField(input, field.Type);
                        input.Enabled = field.Interactive != 2 && options.Mode != "inspect";
                        if (field.Type == 1 || field.Type == 2 || field.Type == 6) input.Enabled = true;
                        FlowLayoutPanel container = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = FieldWidth + 20, Margin = Embedded ? new Padding(0, (int)(4 * scale), 0, (int)(4 * scale)) : new Padding(3, 8, 3, 8) };
                        if ((field.Type == 4 || field.Type == 5 || field.Type == 8) && !String.IsNullOrEmpty(field.Label))
                        {
                            Label caption2 = new Label { Text = field.Label, AutoSize = true };
                            if (Embedded)
                            {
                                caption2.ForeColor = kioskMuted;
                                caption2.Font = KioskFont(11);
                                CentreLabel(caption2);
                            }
                            container.Controls.Add(caption2);
                        }
                        container.Controls.Add(input);
                        container.Visible = FieldData.IsVisible(field.State);
                        fieldData[field.Id] = field;
                        inputs[field.Id] = input; containers[field.Id] = container;
                        fields.Controls.Add(container);
                    }
                }
                finally { fields.ResumeLayout(false); rendering = false; }
                preparingFields = false;
                foreach (Action update in pendingFieldUpdates) update();
                pendingFieldUpdates.Clear();
                ApplyInteractionState();
                FocusPin();
            });
        }

        private void ClearFieldControls()
        {
            for (int index = fields.Controls.Count - 1; index >= 0; index--) fields.Controls[index].Dispose();
            fields.Controls.Clear();
        }

        public void UpdateField(int generation, uint id, string kind, string text, int value)
        {
            Post(delegate
            {
                if (generation != fieldGeneration) return;
                if (fieldUpdatesFailed) return;
                if (preparingFields)
                {
                    if (pendingFieldUpdates.Count >= 512)
                    {
                        canSubmit = false;
                        fieldUpdatesFailed = true;
                        pendingFieldUpdates.Clear();
                        state.Text = "Too many native field updates during initialization. Close this host and retain its log.";
                        ApplyInteractionState();
                        log.Write("Outcome=FieldUpdateOverflow; AuthenticationDisabled=True");
                        return;
                    }
                    pendingFieldUpdates.Add(delegate { ApplyFieldUpdate(id, kind, text, value); });
                    return;
                }
                ApplyFieldUpdate(id, kind, text, value);
                ApplyInteractionState();
                if (kind == "state" || kind == "interactive") FocusPin();
            });
        }

        private void ApplyFieldUpdate(uint id, string kind, string text, int value)
        {
            Control input, container;
            FieldData data;
            if (!inputs.TryGetValue(id, out input) || !containers.TryGetValue(id, out container) || !fieldData.TryGetValue(id, out data)) return;
            rendering = true;
            try
            {
                if (kind == "state") { data.State = value; container.Visible = FieldData.IsVisible(value); }
                else if (kind == "interactive")
                {
                    data.Interactive = value;
                    input.Enabled = value != 2 && options.Mode != "inspect";
                    if (input is TextBox) ((TextBox)input).ReadOnly = value == 1;
                }
                else if (kind == "text") input.Text = text ?? "";
                else if (kind == "checkbox" && input is CheckBox) { ((CheckBox)input).Checked = value != 0; input.Text = text ?? ""; }
                else if (kind == "combo-select" && input is ComboBox)
                {
                    ComboBox combo = (ComboBox)input;
                    if (value >= 0 && value < combo.Items.Count) combo.SelectedIndex = value;
                }
                else if (kind == "combo-delete" && input is ComboBox)
                {
                    ComboBox combo = (ComboBox)input;
                    if (value == -1) combo.Items.Clear(); else if (value >= 0 && value < combo.Items.Count) combo.Items.RemoveAt(value);
                }
                else if (kind == "combo-append" && input is ComboBox) ((ComboBox)input).Items.Add(text ?? "");
                else if (kind == "options" && input is TextBox && (value & 8) != 0) ((TextBox)input).ImeMode = ImeMode.Disable;
            }
            finally { rendering = false; }
        }

        public void ClearSecrets()
        {
            Post(delegate
            {
                rendering = true;
                try { foreach (Control input in inputs.Values) if (input is TextBox && ((TextBox)input).UseSystemPasswordChar) ((TextBox)input).Clear(); }
                finally { rendering = false; }
            });
        }
    }
}
