using System.Diagnostics;
using ShardLauncher.Core;

namespace ShardLauncher;

/// <summary>Small window: status line, progress bar, Play button (Dread decision 2026-09-29).</summary>
internal sealed class MainForm : Form
{
    private readonly LauncherConfig _config;
    private readonly string? _configError;
    private readonly InstallLayout _layout;
    private readonly LauncherLog _log;
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 48, Text = "Starting…", Padding = new Padding(8) };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22, Minimum = 0, Maximum = 1000 };
    private readonly Button _play = new() { Dock = DockStyle.Bottom, Height = 40, Text = "Play", Enabled = false };
    private readonly FlowLayoutPanel _tools = new() { Dock = DockStyle.Bottom, Height = 30, FlowDirection = FlowDirection.RightToLeft };
    private readonly Button _uninstall = new() { Text = "Remove added gumps", AutoSize = true };
    private readonly Button _repair = new() { Text = "Repair", AutoSize = true };
    private readonly Button _previous = new() { Text = "Use previous version", AutoSize = true };
    private readonly RollbackManager _rollback;
    private UpdateResult? _result;

    public MainForm()
    {
        // Server host/port, patch URL and trusted keys are compiled in from the build config (README "Configuring a build").
        _config = LauncherConfig.FromBuild();
        _layout = InstallLayout.Default(_config.DataDirName);
        try
        {
            _config = _config.WithServerOverride(_layout.ServerOverride);
            _config.EnsureComplete();
        }
        catch (LauncherConfigException e) { _configError = e.Message; }
        Text = _config.DisplayName;
        ClientSize = new Size(420, 160);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        _tools.Controls.Add(_uninstall);
        _tools.Controls.Add(_previous);
        _tools.Controls.Add(_repair);
        Controls.Add(_tools);
        Controls.Add(_play);
        Controls.Add(_bar);
        Controls.Add(_status);
        _log = new LauncherLog(_layout.Logs);
        _rollback = new RollbackManager(_layout, _log);
        _play.Click += async (_, _) => await PlayAsync();
        _uninstall.Click += (_, _) => UninstallGumps();
        _repair.Click += async (_, _) => await RunUpdateAsync(repair: true);
        _previous.Click += (_, _) => UsePrevious();
        if (_configError is not null)
        {
            // Fail loudly: no server, patch URL or trusted key was supplied at build time (or server.json is invalid).
            _log.Warn("configuration error: " + _configError);
            _status.Text = "Launcher is not configured. See the error message.";
            _repair.Enabled = _previous.Enabled = _uninstall.Enabled = false;
            Shown += (_, _) =>
            {
                MessageBox.Show(this, _configError, Text + ": configuration error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            };
            return;
        }
        if (_config.ServerOverrideSource is not null) _log.Info($"server address overridden by {_config.ServerOverrideSource}");
        Shown += async (_, _) => await RunUpdateAsync();
    }

    private async Task RunUpdateAsync(bool repair = false)
    {
        _play.Enabled = _repair.Enabled = _previous.Enabled = false;
        var state = LauncherState.Load(_layout.StatePath);
        if (state.UoPath is null || !Directory.Exists(state.UoPath))
        {
            var picked = PickUoFolder();
            if (picked is null) { _status.Text = "No Ultima Online folder selected."; return; }
            state.UoPath = picked;
            state.Save(_layout.StatePath);
        }

        // revert first if the last trial crashed early or local files fail verification.
        var reverted = _rollback.RecoverOnStartup();

        var progress = new Progress<UpdateProgress>(p =>
        {
            _status.Text = p.Stage;
            _bar.Value = p.Total > 0 ? (int)Math.Clamp(p.Done * 1000 / p.Total, 0, 1000) : 0;
        });
        var updater = new Updater(_layout, new HttpsPatchSource(_config.PatchBase!), _config.TrustedKeys,
            typeof(MainForm).Assembly.GetName().Version ?? new Version(1, 0, 0), _log);
        try
        {
            _result = await Task.Run(() => updater.RunAsync(progress, default, repair));
            // Repair when offline / still broken: fall back to a verified earlier version.
            reverted ??= repair ? _rollback.RecoverOnStartup() : null;
        }
        catch (Exception e)
        {
            _log.Warn("updater crashed: " + e);
            _result = new UpdateResult(UpdateOutcome.KeptLastGood, "Update failed: " + e.Message, state.CurrentSerial,
                state.CurrentSerial > 0 && File.Exists(_layout.OverrideFile(state.CurrentSerial)) ? _layout.OverrideFile(state.CurrentSerial) : null,
                0, Array.Empty<string>(), false) { Cuo = new CuoInstaller(_layout, Authenticode.ForPlatform(), _log).VerifyInstalled() };
        }

        _bar.Value = 1000;
        var msg = (reverted is null ? "" : reverted + " ") + _result.Message;
        if (_result.Conflicts.Count > 0) msg += $" ({_result.Conflicts.Count} file(s) skipped, see log)";
        if (_result.LauncherUpdateRequired) msg += " A newer launcher is required.";
        _status.Text = msg;

        // Play only with a hash-verified, Authenticode-verified pinned ClassicUO.
        _repair.Enabled = _previous.Enabled = true;
        if (_result.Cuo.Ok) _play.Enabled = true;
        else _status.Text += Environment.NewLine + _result.Cuo.Message;
    }

    private async Task PlayAsync()
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
        var psi = LaunchCommand.Build(_layout, state.UoPath, _result?.OverrideFile, _config.ServerHost, _config.ServerPort);
        _log.Info($"launching {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
        try
        {
            using var proc = Process.Start(psi) ?? throw new System.ComponentModel.Win32Exception("process did not start");
            var started = DateTimeOffset.UtcNow;
            _rollback.OnLaunched(started);
            Hide();
            // Watch the first 60 s: survive → last_good; non-zero exit → trial marked crashed → revert.
            var exitTask = proc.WaitForExitAsync();
            var exited = await Task.WhenAny(exitTask, Task.Delay(RollbackManager.ConfirmAfter)) == exitTask;
            if (!exited) { _rollback.OnConfirmed(); Close(); return; }
            _rollback.OnExited(DateTimeOffset.UtcNow - started, proc.ExitCode);
            var reverted = _rollback.RecoverOnStartup();
            Show();
            _status.Text = reverted ?? $"ClassicUO closed after {(DateTimeOffset.UtcNow - started).TotalSeconds:0} s (exit code {proc.ExitCode}).";
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _log.Warn("launch failed: " + e.Message);
            MessageBox.Show(this, "Could not start ClassicUO: " + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>remove only gumps we created whose bytes are unchanged; leave everything else.</summary>
    private void UninstallGumps()
    {
        var state = LauncherState.Load(_layout.StatePath);
        if (state.UoPath is null) return;
        if (MessageBox.Show(this, "Remove the gump files this launcher added to your UO Gumps folder?\nFiles you changed and files from other shards are kept.",
                Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
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

    private void UsePrevious()
    {
        if (MessageBox.Show(this, "Switch back to the previous shard art version?\nThe current one will be skipped until a newer release is published.",
                Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _status.Text = _rollback.UsePrevious();
        var s = LauncherState.Load(_layout.StatePath);
        _result = _result is null ? null : _result with
        {
            LaunchSerial = s.CurrentSerial,
            OverrideFile = s.CurrentSerial > 0 && File.Exists(_layout.OverrideFile(s.CurrentSerial)) ? _layout.OverrideFile(s.CurrentSerial) : null,
        };
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
                MessageBox.Show(this, $"Use Ultima Online data folder\n{guess}?", Text, MessageBoxButtons.YesNo) == DialogResult.Yes)
                return guess;

        using var dlg = new FolderBrowserDialog { Description = "Select your Ultima Online data folder (contains tiledata.mul)", UseDescriptionForTitle = true };
        while (dlg.ShowDialog(this) == DialogResult.OK)
        {
            if (File.Exists(Path.Combine(dlg.SelectedPath, "tiledata.mul"))) return dlg.SelectedPath;
            MessageBox.Show(this, "That folder does not contain tiledata.mul.", Text);
        }
        return null;
    }
}
