namespace Kiosk.Client;

internal sealed partial class DesktopView
{
    /// <summary>Account assignment stays inside the existing card. Passwords never enter logs.</summary>
    internal async Task<UserLogon?> AskAccountAsync(Func<bool> cardPresent)
    {
        var completed = new TaskCompletionSource<UserLogon?>(TaskCreationOptions.RunContinuationsAsynchronously);
        askRequest?.TrySetResult(false);
        var cancelled = askRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ApplyVisibility(); ArrangeControls();
        foreach (var child in new Control[] { askTitle, askText, askYes, askNo, pinCountdown }) child.Visible = false;
        using var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = KioskTheme.Surface, ColumnCount = 1, Padding = new Padding(Scale(30)), RowCount = 7 };
        var title = new Label { Text = "Przypisz konto Windows", AutoSize = true, Font = new Font("Segoe UI", 18, FontStyle.Bold), ForeColor = KioskTheme.Text };
        var login = new TextBox { Dock = DockStyle.Top, PlaceholderText = "DOMENA\\użytkownik lub użytkownik@domena", MaxLength = 512 };
        var password = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, MaxLength = 256 };
        var error = new Label { AutoSize = true, ForeColor = Color.FromArgb(255, 107, 107) };
        var save = new Button { Text = "Przypisz konto", AutoSize = true };
        var cancel = new Button { Text = "Anuluj", AutoSize = true };
        KioskTheme.StyleButton(save, true); KioskTheme.StyleButton(cancel);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        buttons.Controls.AddRange([save, cancel]);
        panel.Controls.Add(title); panel.Controls.Add(new Label { Text = "Login", AutoSize = true }); panel.Controls.Add(login);
        panel.Controls.Add(new Label { Text = "Hasło", AutoSize = true }); panel.Controls.Add(password); panel.Controls.Add(error); panel.Controls.Add(buttons);
        card.Controls.Add(panel); panel.BringToFront(); login.Focus();
        cancel.Click += (_, _) => completed.TrySetResult(null);
        save.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(login.Text) || password.TextLength == 0) { error.Text = "Podaj login i hasło."; return; }
            save.Enabled = false; login.Enabled = password.Enabled = false;
            var account = login.Text.Trim(); string? domain = null;
            var slash = account.IndexOf('\\');
            if (slash > 0) { domain = account[..slash]; account = account[(slash + 1)..]; }
            var secret = (password.Text + "\0").ToCharArray(); password.Clear();
            try
            {
                var result = await Task.Run(() => { var value = UserLogon.TryLogon(account, domain, secret, secret.Length - 1, out var code); return (Value: value, Code: code); });
                if (completed.Task.IsCompleted || !cardPresent()) { result.Value?.Dispose(); completed.TrySetResult(null); return; }
                if (result.Value != null) completed.TrySetResult(result.Value);
                else error.Text = "Windows odrzucił login lub hasło (kod " + result.Code + ").";
            }
            catch (Exception ex) { error.Text = "Nie udało się sprawdzić konta: " + ex.GetType().Name; }
            finally { Array.Clear(secret); if (!panel.IsDisposed) { save.Enabled = true; login.Enabled = password.Enabled = true; } }
        };
        using var watch = new System.Windows.Forms.Timer { Interval = 250 };
        watch.Tick += (_, _) => { if (!cardPresent()) completed.TrySetResult(null); };
        watch.Start();
        try
        {
            await Task.WhenAny(completed.Task, cancelled.Task);
            return completed.Task.IsCompleted ? await completed.Task : null;
        }
        finally
        {
            completed.TrySetResult(null);
            password.Clear();
            if (askRequest == cancelled) askRequest = null;
            card.Controls.Remove(panel); ApplyVisibility(); ArrangeControls(); card.Invalidate();
        }
    }
}
