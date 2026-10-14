namespace Klippi.Installer;

public sealed class SetupForm : Form
{
    private readonly bool _uninstall;
    private bool _busy;
    private readonly TextBox _folder = new() { Dock = DockStyle.Top };
    private readonly CheckBox _desktop = new() { Text = "Create a desktop shortcut", AutoSize = true };
    private readonly CheckBox _startMenu = new() { Text = "Create Start menu shortcuts", Checked = true, AutoSize = true };
    private readonly CheckBox _protocol = new() { Text = "Allow keaclip:// links to open this app", Checked = false, AutoSize = true };
    private readonly Label _status = new() { Text = "Settings and saved media stay on your device.", AutoSize = true, MaximumSize = new(485, 0) };
    private readonly Button _install = new() { Text = "Install", AutoSize = true, BackColor = Color.FromArgb(55, 110, 79), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _browse = new() { Text = "Choose folder…", AutoSize = true };
    public SetupForm(bool uninstall)
    {
        _uninstall = uninstall; Text = uninstall ? "Uninstall KeaClip" : "Install KeaClip"; Width = 570; Height = 480; MinimumSize = new(570, 480); MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; Font = new("Segoe UI", 10); BackColor = Color.FromArgb(246, 248, 243);
        if (Environment.ProcessPath is { } self) Icon = Icon.ExtractAssociatedIcon(self);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(26), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(new Label { Text = uninstall ? "Remove the app. Keep your media." : "KeaClip · save, shape, share", AutoSize = true, Font = new("Segoe UI", 18, FontStyle.Bold), Margin = new(0, 0, 0, 14) });
        panel.Controls.Add(new Label { Text = uninstall ? "Only unchanged application files listed in the installation receipt are removed. Settings, changed files and unrelated media are kept." : "A personal Windows installation. No administrator permission, paid services or Windows startup entry is added.", AutoSize = true, MaximumSize = new(485, 0), Margin = new(0, 0, 0, 16) });
        _folder.Width = 480; _folder.Text = uninstall ? Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KeaClip");
        _folder.ReadOnly = uninstall; panel.Controls.Add(_folder); if (!uninstall) panel.Controls.Add(_browse);
        if (!uninstall) { panel.Controls.Add(_desktop); panel.Controls.Add(_startMenu); panel.Controls.Add(_protocol); }
        panel.Controls.Add(_status); _install.Text = uninstall ? "Uninstall app" : "Install / upgrade"; _install.Margin = new(0, 17, 0, 0); panel.Controls.Add(_install); Controls.Add(panel);
        _browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Choose a dedicated empty app folder or the existing KeaClip installation", SelectedPath = _folder.Text, UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath; };
        _install.Click += async (_, _) => await RunAsync();
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Setup is finishing the file transaction. Please wait before closing this window."; } };
    }
    private async Task RunAsync()
    {
        _install.Enabled = false; _browse.Enabled = false; _folder.ReadOnly = true;
        try
        {
            if (_uninstall)
            {
                _ = InstallEngine.ReadReceipt(_folder.Text);
                if (MessageBox.Show(this, "Remove KeaClip application files? Your settings and saved media will be kept.", "Uninstall KeaClip", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                Program.LaunchUninstallWorker(_folder.Text); Close(); return;
            }
            _busy = true; _desktop.Enabled = false; _startMenu.Enabled = false; _protocol.Enabled = false;
            var target = _folder.Text; var choices = new InstallChoices(_desktop.Checked, _startMenu.Checked, _protocol.Checked); _status.Text = "Checking the package and preparing a safe installation…";
            var completion = new TaskCompletionSource<InstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => { try { using var payload = Program.Payload(); completion.TrySetResult(new InstallEngine(new WindowsIntegration()).Install(payload, target, Environment.ProcessPath!, choices)); } catch (Exception error) { completion.TrySetException(error); } });
            thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = false; thread.Start();
            var result = await completion.Task; _status.Text = "Installed successfully. Open KeaClip from the chosen folder or shortcut. " + result.Warning; _install.Text = "Installed";
        }
        catch (Exception error) { _status.Text = "Setup could not finish: " + error.Message; _install.Enabled = true; }
        finally { _busy = false; _desktop.Enabled = true; _startMenu.Enabled = true; _protocol.Enabled = true; _browse.Enabled = !_uninstall; _folder.ReadOnly = _uninstall; if (_uninstall) _install.Enabled = true; }
    }
}
