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
    private UpdateResult? _result;

    public MainForm()
    {
        Text = "Unknown Shard";
        ClientSize = new Size(420, 130);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_play);
        Controls.Add(_bar);
        Controls.Add(_status);
        _log = new LauncherLog(_layout.Logs);
        _play.Click += (_, _) => Play();
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
                0, Array.Empty<string>(), false);
        }

        _bar.Value = 1000;
        var msg = _result.Message;
        if (_result.Conflicts.Count > 0) msg += $" ({_result.Conflicts.Count} file(s) skipped, see log)";
        if (_result.LauncherUpdateRequired) msg += " A newer launcher is required.";
        _status.Text = msg;

        if (File.Exists(_layout.CuoExe)) _play.Enabled = true;
        else _status.Text += Environment.NewLine + "ClassicUO is not installed yet (cuo\\ClassicUO.exe).";
    }

    private void Play()
    {
        var state = LauncherState.Load(_layout.StatePath);
        if (state.UoPath is null) return;
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
