using System.Diagnostics;
using UnknownShard.Launcher.Core;

namespace UnknownShardLauncher;

/// <summary>Small window: status line, progress bar, Play button (Dread decision 2026-09-29).</summary>
internal sealed class MainForm : Form
{
    private readonly InstallLayout _layout = InstallLayout.Default();
    private readonly LauncherLog _log;
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 48, Text = "Starting…", Padding = new Padding(8) };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22, Minimum = 0, Maximum = 1000 };
    private readonly Button _play = new() { Dock = DockStyle.Bottom, Height = 40, Text = "Play", Enabled = false };
    private readonly FlowLayoutPanel _tools = new() { Dock = DockStyle.Bottom, Height = 30, FlowDirection = FlowDirection.RightToLeft };
    private readonly Button _uninstall = new() { Text = "Remove shard gumps", AutoSize = true };
    private UpdateResult? _result;

    public MainForm()
    {
        Text = "Unknown Shard";
        ClientSize = new Size(420, 160);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        _tools.Controls.Add(_uninstall);
        Controls.Add(_tools);
        Controls.Add(_play);
        Controls.Add(_bar);
        Controls.Add(_status);
        _log = new LauncherLog(_layout.Logs);
        _play.Click += (_, _) => Play();
        _uninstall.Click += (_, _) => UninstallGumps();
        Shown += async (_, _) => await RunUpdateAsync();
    }

    private async Task RunUpdateAsync()
    {
        var state = LauncherState.Load(_layout.StatePath);
        if (state.UoPath is null || !Directory.Exists(state.UoPath))
        {
            var picked = PickUoFolder();
            if (picked is null) { _status.Text = "No Ultima Online folder selected."; return; }
            state.UoPath = picked;
            state.Save(_layout.StatePath);
        }

        var progress = new Progress<UpdateProgress>(p =>
        {
            _status.Text = p.Stage;
            _bar.Value = p.Total > 0 ? (int)Math.Clamp(p.Done * 1000 / p.Total, 0, 1000) : 0;
        });
        var updater = new Updater(_layout, new HttpsPatchSource(HttpsPatchSource.DefaultBase), TrustedKeys.Load(),
            typeof(MainForm).Assembly.GetName().Version ?? new Version(1, 0, 0), _log);
        try
        {
            _result = await Task.Run(() => updater.RunAsync(progress));
        }
        catch (Exception e)
        {
            _log.Warn("updater crashed: " + e);
            _result = new UpdateResult(UpdateOutcome.KeptLastGood, "Update failed: " + e.Message, state.CurrentSerial,
                state.CurrentSerial > 0 && File.Exists(_layout.OverrideFile(state.CurrentSerial)) ? _layout.OverrideFile(state.CurrentSerial) : null,
                0, Array.Empty<string>(), false) { Cuo = new CuoInstaller(_layout, Authenticode.ForPlatform(), _log).VerifyInstalled() };
        }

        _bar.Value = 1000;
        var msg = _result.Message;
        if (_result.Conflicts.Count > 0) msg += $" ({_result.Conflicts.Count} file(s) skipped, see log)";
        if (_result.LauncherUpdateRequired) msg += " A newer launcher is required.";
        _status.Text = msg;

        // unknown-shard#194: Play only with a hash-verified, Authenticode-verified pinned ClassicUO.
        if (_result.Cuo.Ok) _play.Enabled = true;
        else _status.Text += Environment.NewLine + _result.Cuo.Message;
    }

    private void Play()
    {
        var state = LauncherState.Load(_layout.StatePath);
        if (state.UoPath is null) return;
        var cuo = new CuoInstaller(_layout, Authenticode.ForPlatform(), _log).VerifyInstalled();
        if (!cuo.Ok)
        {
            _log.Warn(cuo.Message);
            _status.Text = cuo.Message;
            _play.Enabled = false;
            return;
        }
        Directory.CreateDirectory(_layout.Profiles);
        var psi = LaunchCommand.Build(_layout, state.UoPath, _result?.OverrideFile);
        _log.Info($"launching {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
        try
        {
            Process.Start(psi);
            Close();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _log.Warn("launch failed: " + e.Message);
            MessageBox.Show(this, "Could not start ClassicUO: " + e.Message, "Unknown Shard", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>unknown-shard#193: remove only gumps we created whose bytes are unchanged; leave everything else.</summary>
    private void UninstallGumps()
    {
        var state = LauncherState.Load(_layout.StatePath);
        if (state.UoPath is null) return;
        if (MessageBox.Show(this, "Remove the gump files this launcher added to your UO Gumps folder?\nFiles you changed and files from other shards are kept.",
                "Unknown Shard", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            var r = GumpLedger.Load(Path.Combine(state.UoPath, "Gumps")).Uninstall();
            _log.Info($"uninstall gumps: removed [{string.Join(", ", r.Removed)}], kept [{string.Join(", ", r.Kept)}]");
            _status.Text = $"Removed {r.Removed.Count} gump(s); kept {r.Kept.Count} changed file(s).";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn("uninstall failed: " + e.Message);
            _status.Text = "Uninstall failed: " + e.Message;
        }
    }

    private string? PickUoFolder()
    {
        foreach (var guess in new[]
                 {
                     @"C:\Program Files (x86)\Electronic Arts\Ultima Online Classic",
                     @"C:\Program Files\Electronic Arts\Ultima Online Classic",
                     @"C:\Program Files (x86)\Ultima Online Classic",
                 })
            if (File.Exists(Path.Combine(guess, "tiledata.mul")) &&
                MessageBox.Show(this, $"Use Ultima Online data folder\n{guess}?", "Unknown Shard", MessageBoxButtons.YesNo) == DialogResult.Yes)
                return guess;

        using var dlg = new FolderBrowserDialog { Description = "Select your Ultima Online data folder (contains tiledata.mul)", UseDescriptionForTitle = true };
        while (dlg.ShowDialog(this) == DialogResult.OK)
        {
            if (File.Exists(Path.Combine(dlg.SelectedPath, "tiledata.mul"))) return dlg.SelectedPath;
            MessageBox.Show(this, "That folder does not contain tiledata.mul.", "Unknown Shard");
        }
        return null;
    }
}
