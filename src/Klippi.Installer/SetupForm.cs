using System.Diagnostics;

namespace Klippi.Installer;

/// <summary>
/// The install and uninstall window, in KeaClip's limestone-day colours: one short explanation, the few choices
/// that matter, and one kea-orange main button. After installing, the same button opens KeaClip.
/// </summary>
public sealed class SetupForm : Form
{
    private static readonly Color Background = Color.FromArgb(0xF3, 0xF0, 0xE8), Text1 = Color.FromArgb(0x22, 0x24, 0x1D),
        Muted = Color.FromArgb(0x5C, 0x5E, 0x54), Warm = Color.FromArgb(0xB8, 0x49, 0x1A), Danger = Color.FromArgb(0xB0, 0x3A, 0x2F);
    private readonly bool _uninstall;
    private bool _busy;
    private string? _installedApp;
    private readonly TextBox _folder = new() { Width = 490, BorderStyle = BorderStyle.FixedSingle };
    private readonly CheckBox _desktop = new() { Text = "Add a desktop shortcut", AutoSize = true };
    private readonly CheckBox _startMenu = new() { Text = "Add to the Start menu", Checked = true, AutoSize = true };
    private readonly CheckBox _protocol = new() { Text = "Let browser buttons send links to KeaClip (keaclip:// links)", Checked = false, AutoSize = true };
    private readonly CheckBox _removeSettings = new() { Text = "Also remove my settings and history", Checked = false, AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new(490, 0), Margin = new(0, 14, 0, 0) };
    private readonly Button _main = new() { AutoSize = true, MinimumSize = new(170, 40), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new("Segoe UI Semibold", 10.5f), Cursor = Cursors.Hand, Margin = new(0, 18, 0, 0) };
    private readonly LinkLabel _browse = new() { Text = "Choose another folder", AutoSize = true, LinkColor = Warm, ActiveLinkColor = Warm, Margin = new(0, 4, 0, 10) };

    public SetupForm(bool uninstall)
    {
        _uninstall = uninstall;
        Text = uninstall ? "Uninstall KeaClip" : "Install KeaClip";
        ClientSize = new(560, uninstall ? 330 : 470); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen; Font = new("Segoe UI", 10); BackColor = Background; ForeColor = Text1;
        if (Environment.ProcessPath is { } self) Icon = Icon.ExtractAssociatedIcon(self);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(30, 26, 30, 20), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        // Large text carries extra side bearing: nudge the heading left so it lines up with the text below.
        panel.Controls.Add(new Label { Text = uninstall ? "Remove KeaClip" : "Install KeaClip", AutoSize = true, Font = new("Segoe UI Semibold", 20), Margin = new(-4, 0, 0, 6) });
        panel.Controls.Add(new Label
        {
            Text = uninstall
                ? "Removes the app and the tools it downloaded (FFmpeg, yt-dlp, Deno). Your videos and other saved media are never touched."
                : "Installs just for you: no administrator rights, nothing added to Windows startup. Your settings stay when you update.\n\n"
                  + "On its first start KeaClip downloads its free helper tools (yt-dlp, FFmpeg, Deno), and once a day it checks GitHub for updates. You can turn the daily check off in Settings. No tracking, no ads.",
            AutoSize = true, MaximumSize = new(490, 0), ForeColor = Muted, Margin = new(0, 0, 0, 18),
        });
        panel.Controls.Add(new Label { Text = uninstall ? "Installed in" : "Install to", AutoSize = true, Font = new("Segoe UI Semibold", 9.5f), Margin = new(0, 0, 0, 4) });
        _folder.Text = uninstall ? Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KeaClip");
        _folder.ReadOnly = uninstall; _folder.BackColor = Color.White;
        panel.Controls.Add(_folder);
        if (uninstall) { _removeSettings.Margin = new(0, 14, 0, 0); panel.Controls.Add(_removeSettings); }
        else { panel.Controls.Add(_browse); panel.Controls.Add(_startMenu); panel.Controls.Add(_desktop); panel.Controls.Add(_protocol); }
        _main.Text = uninstall ? "Remove KeaClip" : "Install";
        _main.BackColor = uninstall ? Danger : Warm; _main.FlatAppearance.BorderSize = 0;
        panel.Controls.Add(_main); panel.Controls.Add(_status);
        Controls.Add(panel);
        AcceptButton = _main;
        // Start on the main button, not inside the path box (which would show the path selected).
        Shown += (_, _) => { _folder.SelectionLength = 0; ActiveControl = _main; };
        _browse.LinkClicked += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Choose an empty folder for KeaClip, or the folder it is already installed in", SelectedPath = _folder.Text, UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath; };
        _main.Click += async (_, _) => { if (_installedApp is { } app) OpenAndClose(app); else await RunAsync(); };
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Almost done. Please wait a moment before closing."; } };
    }

    private void OpenAndClose(string app)
    {
        try { Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app)! }); Close(); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { _status.Text = "KeaClip is installed, but could not be opened from here. Open it from the Start menu."; }
    }

    private async Task RunAsync()
    {
        _main.Enabled = false; _browse.Enabled = false; _folder.ReadOnly = true;
        try
        {
            if (_uninstall)
            {
                _ = InstallEngine.ReadReceipt(_folder.Text);
                var question = _removeSettings.Checked ? "Remove KeaClip, its downloaded tools, and your settings and history? Your videos are kept." : "Remove KeaClip and its downloaded tools? Your settings, history and videos are kept.";
                if (MessageBox.Show(this, question, "Remove KeaClip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Program.LaunchUninstallWorker(_folder.Text, removeSettings: _removeSettings.Checked); Close(); return;
            }
            _busy = true; _desktop.Enabled = false; _startMenu.Enabled = false; _protocol.Enabled = false;
            UpgradeMode.RefuseDowngrade(_folder.Text);
            var target = _folder.Text; var choices = new InstallChoices(_desktop.Checked, _startMenu.Checked, _protocol.Checked); _status.ForeColor = Muted; _status.Text = "Installing…";
            var completion = new TaskCompletionSource<InstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => { try { using var payload = Program.Payload(); completion.TrySetResult(new InstallEngine(new WindowsIntegration()).Install(payload, target, Environment.ProcessPath!, choices)); } catch (Exception error) { completion.TrySetException(error); } });
            thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = false; thread.Start();
            var result = await completion.Task;
            try { UpgradeMode.PruneOlderBackups(result.Root, result.Backup); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            _installedApp = Path.Combine(result.Root, "KeaClip.exe");
            _status.Text = "KeaClip is installed." + (string.IsNullOrWhiteSpace(result.Warning) ? "" : " " + result.Warning);
            _main.Text = "Open KeaClip"; _main.Enabled = true; _main.Focus();
        }
        catch (Exception error) { _status.ForeColor = Danger; _status.Text = "Setup could not finish: " + error.Message; _main.Enabled = true; }
        finally { _busy = false; _desktop.Enabled = true; _startMenu.Enabled = true; _protocol.Enabled = true; _browse.Enabled = !_uninstall && _installedApp is null; _folder.ReadOnly = _uninstall || _installedApp is not null; if (_uninstall) _main.Enabled = true; }
    }
}
