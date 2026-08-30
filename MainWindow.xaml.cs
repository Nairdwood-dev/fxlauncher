using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Nairdwood.Launcher.Models;
using Nairdwood.Launcher.Services;

namespace Nairdwood.Launcher;

public partial class MainWindow : Window
{
    private const int MaximumConsoleParagraphs = 12_000;
    private readonly SettingsService _settingsService = new();
    private readonly ConsoleProcessHost _host = new();
    private readonly FxServerRconClient _rcon = new();
    private readonly MariaDbService _mariaDb = new();
    private readonly DispatcherTimer _runtimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _mariaDbTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly StringBuilder _plainLog = new();
    private readonly List<string> _commandHistory = new();
    private LauncherSettings _settings = new();
    private MariaDbStatus _mariaDbStatus = new(null, "MariaDB", MariaDbState.Unknown);
    private StreamWriter? _sessionLog;
    private DateTime? _startedAt;
    private int _historyIndex;
    private int _searchParagraphIndex = -1;
    private string _lastSearch = string.Empty;
    private bool _closing;
    private bool _restartInProgress;
    private bool _shutdownFinalized;
    private bool _settingsResetPending;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.ApplyDarkChrome(this);
        _host.OutputReceived += Host_OutputReceived;
        _host.ProcessExited += Host_ProcessExited;
        _runtimeTimer.Tick += RuntimeTimer_Tick;
        _mariaDbTimer.Tick += async (_, _) => await RefreshMariaDbStatusAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();
        PopulateSettingsFields();
        SettingsPanel.Visibility = Visibility.Collapsed;
        SetupOverlay.Visibility = Visibility.Collapsed;
        ResetSettingsOverlay.Visibility = Visibility.Collapsed;
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        UpdateCommandAvailability();

        AppendConsole("Nairdwood Launcher ready. Select an executable and press Start Server.", ConsoleStream.Launcher);
        await RefreshMariaDbStatusAsync();
        _mariaDbTimer.Start();

        if (!_settings.SetupCompleted && string.IsNullOrWhiteSpace(_settings.ExecutablePath))
            ShowSetup();
    }

    private void PopulateSettingsFields()
    {
        ExecutablePathBox.Text = _settings.ExecutablePath;
        ArgumentsBox.Text = _settings.Arguments;
        WorkingDirectoryBox.Text = _settings.WorkingDirectory;
        ConfigPathBox.Text = _settings.ConfigPath;
        RconHostBox.Text = _settings.RconHost;
        RconPortBox.Text = _settings.RconPort.ToString();
        RconPasswordBox.Password = _settings.RconPassword;
        AutoRestartCheckBox.IsChecked = _settings.AutoRestart;
        AutoScrollCheckBox.IsChecked = _settings.AutoScroll;
        UpdateCommandAvailability();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Select FXServer or launch script",
            Filter = "Launchable files (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Executable files (*.exe)|*.exe|Batch files (*.bat;*.cmd)|*.bat;*.cmd|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(ExecutablePathBox.Text))
        {
            var existingDirectory = Path.GetDirectoryName(ExecutablePathBox.Text);
            if (Directory.Exists(existingDirectory)) picker.InitialDirectory = existingDirectory;
        }

        if (picker.ShowDialog(this) != true) return;
        ExecutablePathBox.Text = picker.FileName;
        if (string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text))
            WorkingDirectoryBox.Text = Path.GetDirectoryName(picker.FileName) ?? string.Empty;
    }

    private string? PickConfigFile(string currentPath)
    {
        var picker = new OpenFileDialog
        {
            Title = "Select the FXServer configuration file",
            Filter = "FXServer config (*.cfg)|*.cfg|Configuration files (*.conf;*.ini;*.txt)|*.conf;*.ini;*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
            FileName = Path.GetFileName(currentPath)
        };
        var directory = Path.GetDirectoryName(currentPath);
        if (Directory.Exists(directory)) picker.InitialDirectory = directory;
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }

    private void ConfigBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickConfigFile(ConfigPathBox.Text);
        if (path is not null) ConfigPathBox.Text = path;
    }

    private void ReadRconButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var password = ServerConfigService.ReadRconPassword(ConfigPathBox.Text.Trim());
            if (password is null)
            {
                SettingsFeedbackText.Text = "No active rcon_password entry was found in this config.";
                return;
            }
            RconPasswordBox.Password = password;
            SettingsFeedbackText.Text = "RCON password loaded from the selected config.";
        }
        catch (Exception exception)
        {
            SettingsFeedbackText.Text = exception.Message;
        }
    }

    private void WriteRconButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ServerConfigService.WriteRconPassword(ConfigPathBox.Text.Trim(), RconPasswordBox.Password);
            SettingsFeedbackText.Text = "RCON password written to the config. The original was preserved as a .nairdwood-backup file.";
            CaptureAndSaveSettings();
        }
        catch (Exception exception)
        {
            SettingsFeedbackText.Text = exception.Message;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        await RefreshMariaDbStatusAsync();

        if (_mariaDbStatus.State == MariaDbState.Running)
        {
            StartServer();
            return;
        }

        StartButton.IsEnabled = true;
        MariaDbWarningDetailText.Text = _mariaDbStatus.State switch
        {
            MariaDbState.NotInstalled => "No installed MariaDB Windows service was detected.",
            MariaDbState.Stopped => $"{_mariaDbStatus.DisplayName} is installed, but its service is currently stopped.",
            _ => "The launcher could not confirm that the MariaDB service is running."
        };
        MariaDbWarningOverlay.Visibility = Visibility.Visible;
    }

    private void MariaDbWarningCancelButton_Click(object sender, RoutedEventArgs e)
    {
        MariaDbWarningOverlay.Visibility = Visibility.Collapsed;
        StartButton.Focus();
    }

    private void MariaDbStartAnywayButton_Click(object sender, RoutedEventArgs e)
    {
        MariaDbWarningOverlay.Visibility = Visibility.Collapsed;
        StartServer();
    }

    private void StartServer()
    {
        try
        {
            CaptureAndSaveSettings();
            StartSessionLog();
            _host.Start(_settings);
            _startedAt = DateTime.Now;
            _runtimeTimer.Start();
            SetRunningUi(true);
            FooterStatusText.Text = "FXServer is running. Console input is active.";
        }
        catch (Exception exception)
        {
            CloseSessionLog();
            SetRunningUi(false);
            AppendConsole($"Unable to start: {exception.Message}", ConsoleStream.Error);
            MessageBox.Show(this, exception.Message, "Unable to start server", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        FooterStatusText.Text = "Requesting a graceful shutdown...";
        try
        {
            await _host.StopAsync(force: false);
        }
        catch (Exception exception)
        {
            AppendConsole($"Stop failed: {exception.Message}", ConsoleStream.Error);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        PopulateSettingsFields();
        SettingsFeedbackText.Text = string.Empty;
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void SettingsCancelButton_Click(object sender, RoutedEventArgs e)
    {
        PopulateSettingsFields();
        SettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void SettingsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureAndSaveSettings();
        SettingsPanel.Visibility = Visibility.Collapsed;
        FooterStatusText.Text = "Launcher settings saved.";
    }

    private void RunSetupButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        ShowSetup();
    }

    private void ShowSetup()
    {
        SetupExecutablePathBox.Text = ExecutablePathBox.Text;
        SetupConfigPathBox.Text = ConfigPathBox.Text;
        SetupRconPasswordBox.Password = RconPasswordBox.Password;
        SetupSkipRconCheckBox.IsChecked = string.IsNullOrWhiteSpace(RconPasswordBox.Password);
        SetupFeedbackText.Text = string.Empty;
        UpdateSetupMariaDbUi();
        SetupOverlay.Visibility = Visibility.Visible;
    }

    private void SetupExecutableBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Select FXServer or launch script",
            Filter = "Launchable files (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All files (*.*)|*.*",
            CheckFileExists = true
        };
        var directory = Path.GetDirectoryName(SetupExecutablePathBox.Text);
        if (Directory.Exists(directory)) picker.InitialDirectory = directory;
        if (picker.ShowDialog(this) == true) SetupExecutablePathBox.Text = picker.FileName;
    }

    private void SetupConfigBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PickConfigFile(SetupConfigPathBox.Text);
        if (path is null) return;
        SetupConfigPathBox.Text = path;
        try
        {
            var password = ServerConfigService.ReadRconPassword(path);
            if (password is null) return;
            SetupRconPasswordBox.Password = password;
            SetupSkipRconCheckBox.IsChecked = false;
            SetupFeedbackText.Text = "Existing RCON password detected and loaded from the config.";
        }
        catch (Exception exception)
        {
            SetupFeedbackText.Text = exception.Message;
        }
    }

    private void GenerateRconButton_Click(object sender, RoutedEventArgs e)
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        SetupRconPasswordBox.Password = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        SetupSkipRconCheckBox.IsChecked = false;
    }

    private void SetupSkipRconCheckBox_Changed(object sender, RoutedEventArgs e) =>
        SetupRconPasswordBox.IsEnabled = SetupSkipRconCheckBox.IsChecked != true;

    private async void SetupMariaDbButton_Click(object sender, RoutedEventArgs e)
    {
        SetupMariaDbButton.IsEnabled = false;
        try
        {
            await _mariaDb.ToggleAsync(_mariaDbStatus);
            if (_mariaDbStatus.IsInstalled) await Task.Delay(800);
            await RefreshMariaDbStatusAsync();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            SetupFeedbackText.Text = "MariaDB service change cancelled.";
        }
        catch (Exception exception)
        {
            SetupFeedbackText.Text = exception.Message;
        }
        finally
        {
            SetupMariaDbButton.IsEnabled = true;
            UpdateSetupMariaDbUi();
        }
    }

    private void UpdateSetupMariaDbUi()
    {
        SetupMariaDbStatusText.Text = _mariaDbStatus.State switch
        {
            MariaDbState.Running => $"{_mariaDbStatus.DisplayName} is running and ready.",
            MariaDbState.Stopped => $"{_mariaDbStatus.DisplayName} is installed but stopped.",
            MariaDbState.NotInstalled => "MariaDB is not installed on this computer.",
            _ => "MariaDB status could not be determined."
        };
        SetupMariaDbButton.Content = _mariaDbStatus.State switch
        {
            MariaDbState.Running => "Stop MariaDB",
            MariaDbState.Stopped => "Start MariaDB",
            MariaDbState.NotInstalled => "Download MariaDB",
            _ => "Check MariaDB"
        };
    }

    private void SetupFinishButton_Click(object sender, RoutedEventArgs e)
    {
        var executable = SetupExecutablePathBox.Text.Trim();
        var configPath = SetupConfigPathBox.Text.Trim();
        if (!File.Exists(executable))
        {
            SetupFeedbackText.Text = "Select an existing FXServer executable or launch script.";
            return;
        }
        if (!File.Exists(configPath))
        {
            SetupFeedbackText.Text = "Select an existing server configuration file.";
            return;
        }

        var skipRcon = SetupSkipRconCheckBox.IsChecked == true;
        if (!skipRcon)
        {
            try
            {
                ServerConfigService.WriteRconPassword(configPath, SetupRconPasswordBox.Password);
            }
            catch (Exception exception)
            {
                SetupFeedbackText.Text = exception.Message;
                return;
            }
        }

        ExecutablePathBox.Text = executable;
        ConfigPathBox.Text = configPath;
        if (string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text))
            WorkingDirectoryBox.Text = Path.GetDirectoryName(executable) ?? string.Empty;
        RconPasswordBox.Password = skipRcon ? string.Empty : SetupRconPasswordBox.Password;
        _settings.SetupCompleted = true;
        CaptureAndSaveSettings();
        SetupOverlay.Visibility = Visibility.Collapsed;
        FooterStatusText.Text = "Nairdwood Launcher setup completed.";
    }

    private void SetupSkipButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.SetupCompleted = true;
        CaptureAndSaveSettings();
        SetupOverlay.Visibility = Visibility.Collapsed;
    }

    private void SetupCancelButton_Click(object sender, RoutedEventArgs e) => SetupOverlay.Visibility = Visibility.Collapsed;

    private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host.IsRunning)
        {
            SettingsFeedbackText.Text = "Stop the server before resetting launcher settings.";
            return;
        }
        ResetSettingsOverlay.Visibility = Visibility.Visible;
    }

    private void ResetSettingsCancelButton_Click(object sender, RoutedEventArgs e) =>
        ResetSettingsOverlay.Visibility = Visibility.Collapsed;

    private void ResetSettingsConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.Reset();
        _settings = new LauncherSettings();
        _settingsResetPending = true;
        PopulateSettingsFields();
        ResetSettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        FooterStatusText.Text = "Saved launcher settings were reset. Session logs were preserved.";
        ShowSetup();
    }

    private void RconPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) => UpdateCommandAvailability();

    private void UpdateCommandAvailability()
    {
        var configured = !string.IsNullOrWhiteSpace(RconPasswordBox.Password);
        CommandBox.IsEnabled = configured;
        SendCommandButton.IsEnabled = configured;
        CommandUnavailableText.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
    }

    private void KillButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.IsRunning) return;

        ModalOverlay.Visibility = Visibility.Visible;
        KillConfirmButton.Focus();
    }

    private void KillCancelButton_Click(object sender, RoutedEventArgs e) => CloseKillModal();

    private void CloseKillModal()
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        KillButton.Focus();
    }

    private async void KillConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_host.IsRunning)
        {
            CloseKillModal();
            return;
        }

        ModalOverlay.Visibility = Visibility.Collapsed;
        KillConfirmButton.IsEnabled = false;
        KillButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        FooterStatusText.Text = "Killing the server process tree...";
        AppendConsole("Force-kill requested by the launcher operator.", ConsoleStream.Launcher);

        try
        {
            await _host.StopAsync(force: true);
        }
        catch (Exception exception)
        {
            AppendConsole($"Force-kill failed: {exception.Message}", ConsoleStream.Error);
            if (_host.IsRunning)
            {
                KillButton.IsEnabled = true;
                StopButton.IsEnabled = true;
            }
        }
        finally
        {
            KillConfirmButton.IsEnabled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (CloseModalOverlay.Visibility == Visibility.Visible && !_closing)
        {
            CloseModalOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (ResetSettingsOverlay.Visibility == Visibility.Visible)
        {
            ResetSettingsOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (SetupOverlay.Visibility == Visibility.Visible)
        {
            SetupOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (SettingsPanel.Visibility == Visibility.Visible)
        {
            SettingsCancelButton_Click(sender, e);
            e.Handled = true;
        }
        else if (MariaDbWarningOverlay.Visibility == Visibility.Visible)
        {
            MariaDbWarningOverlay.Visibility = Visibility.Collapsed;
            StartButton.Focus();
            e.Handled = true;
        }
        else if (ModalOverlay.Visibility == Visibility.Visible)
        {
            CloseKillModal();
            e.Handled = true;
        }
    }

    private void CloseCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        CloseModalOverlay.Visibility = Visibility.Collapsed;
    }

    private async void CloseConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;

        _closing = true;
        CloseCancelButton.IsEnabled = false;
        CloseConfirmButton.IsEnabled = false;
        CloseConfirmButton.Content = "Stopping...";
        FooterStatusText.Text = "Stopping the server process tree before exit...";
        AppendConsole("Launcher exit confirmed; stopping the complete server process tree.", ConsoleStream.Launcher);

        try
        {
            await _host.StopAsync(force: true);
            Close();
        }
        catch (Exception exception)
        {
            AppendConsole($"Unable to stop the server before exit: {exception.Message}", ConsoleStream.Error);
            FooterStatusText.Text = "The launcher stayed open because the server could not be stopped.";
            _closing = false;
            CloseCancelButton.IsEnabled = true;
            CloseConfirmButton.IsEnabled = true;
            CloseConfirmButton.Content = "Try Again";
        }
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_restartInProgress) return;
        _restartInProgress = true;
        RestartButton.IsEnabled = false;
        FooterStatusText.Text = "Restarting FXServer...";

        try
        {
            await _host.StopAsync(force: false);
            await Task.Delay(700);
            if (!_closing) StartServer();
        }
        catch (Exception exception)
        {
            AppendConsole($"Restart failed: {exception.Message}", ConsoleStream.Error);
        }
        finally
        {
            _restartInProgress = false;
            RestartButton.IsEnabled = _host.IsRunning;
        }
    }

    private async void SendCommandButton_Click(object sender, RoutedEventArgs e) => await SendCurrentCommandAsync();

    private async void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SendCurrentCommandAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && _commandHistory.Count > 0)
        {
            _historyIndex = Math.Max(0, _historyIndex - 1);
            CommandBox.Text = _commandHistory[_historyIndex];
            CommandBox.CaretIndex = CommandBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _commandHistory.Count > 0)
        {
            _historyIndex = Math.Min(_commandHistory.Count, _historyIndex + 1);
            CommandBox.Text = _historyIndex == _commandHistory.Count ? string.Empty : _commandHistory[_historyIndex];
            CommandBox.CaretIndex = CommandBox.Text.Length;
            e.Handled = true;
        }
    }

    private async Task SendCurrentCommandAsync()
    {
        var command = CommandBox.Text.Trim();
        if (command.Length == 0) return;
        if (string.IsNullOrWhiteSpace(RconPasswordBox.Password))
        {
            UpdateCommandAvailability();
            return;
        }

        SendCommandButton.IsEnabled = false;
        try
        {
            if (!int.TryParse(RconPortBox.Text, out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException("RCON port must be a number between 1 and 65535.");

            var response = await _rcon.SendCommandAsync(
                RconHostBox.Text.Trim(), port, RconPasswordBox.Password, command);

            if (response.Contains("Invalid password", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("FXServer rejected the RCON password.");
            if (response.Contains("must set rcon_password", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("FXServer has RCON disabled. Add set rcon_password to server.cfg and restart the server.");

            AppendConsole($"> {command}", ConsoleStream.Launcher);
            foreach (var line in response.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                AppendConsole($"[RCON] {line}", ConsoleStream.Standard);

            if (_commandHistory.Count == 0 || _commandHistory[^1] != command)
                _commandHistory.Add(command);
            _historyIndex = _commandHistory.Count;
            CommandBox.Clear();
            FooterStatusText.Text = "Command executed through FXServer RCON.";
        }
        catch (Exception exception)
        {
            AppendConsole($"Command failed: {exception.Message}", ConsoleStream.Error);
            FooterStatusText.Text = "Command was not executed. Check the RCON settings.";
        }
        finally
        {
            SendCommandButton.IsEnabled = !string.IsNullOrWhiteSpace(RconPasswordBox.Password);
            if (CommandBox.IsEnabled) CommandBox.Focus();
        }
    }

    private async void MariaDbButton_Click(object sender, RoutedEventArgs e)
    {
        MariaDbButton.IsEnabled = false;
        try
        {
            if (!_mariaDbStatus.IsInstalled)
            {
                await _mariaDb.ToggleAsync(_mariaDbStatus);
                AppendConsole("MariaDB is not installed; opened the official download page.", ConsoleStream.Launcher);
                return;
            }

            var action = _mariaDbStatus.State == MariaDbState.Running ? "Stopping" : "Starting";
            FooterStatusText.Text = $"{action} {_mariaDbStatus.DisplayName}...";
            await _mariaDb.ToggleAsync(_mariaDbStatus);
            await Task.Delay(800);
            await RefreshMariaDbStatusAsync();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            FooterStatusText.Text = "MariaDB service change cancelled.";
        }
        catch (Exception exception)
        {
            AppendConsole($"MariaDB control failed: {exception.Message}", ConsoleStream.Error);
            MessageBox.Show(this, exception.Message, "MariaDB control failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            MariaDbButton.IsEnabled = true;
        }
    }

    private async Task RefreshMariaDbStatusAsync()
    {
        try
        {
            _mariaDbStatus = await _mariaDb.GetStatusAsync();
            switch (_mariaDbStatus.State)
            {
                case MariaDbState.NotInstalled:
                    MariaDbButton.Content = "Download MariaDB";
                    break;
                case MariaDbState.Running:
                    MariaDbButton.Content = "Stop MariaDB";
                    break;
                case MariaDbState.Stopped:
                    MariaDbButton.Content = "Start MariaDB";
                    break;
                default:
                    MariaDbButton.Content = $"MariaDB: {_mariaDbStatus.State}";
                    break;
            }
            UpdateSetupMariaDbUi();
        }
        catch (Exception exception)
        {
            _mariaDbStatus = new MariaDbStatus(null, "MariaDB", MariaDbState.Unknown);
            MariaDbButton.Content = "MariaDB unavailable";
            FooterStatusText.Text = exception.Message;
            UpdateSetupMariaDbUi();
        }
    }

    private void Host_OutputReceived(object? sender, ConsoleOutputEventArgs args) =>
        Dispatcher.InvokeAsync(() => AppendConsole(args.Text, args.Stream));

    private void Host_ProcessExited(object? sender, ProcessExitedEventArgs args) =>
        Dispatcher.InvokeAsync(async () =>
        {
            // A fast restart may already own a new process before the old process's dispatcher
            // notification is rendered. Never let that stale notification close the new log or
            // reset its controls back to OFFLINE.
            if (_host.IsRunning) return;

            _runtimeTimer.Stop();
            _startedAt = null;
            CloseSessionLog();
            ModalOverlay.Visibility = Visibility.Collapsed;
            CloseModalOverlay.Visibility = Visibility.Collapsed;
            SetRunningUi(false);
            FooterStatusText.Text = args.ExitCode == 0 ? "FXServer stopped." : $"FXServer exited with code {args.ExitCode}.";

            if (!args.Expected && !_closing && AutoRestartCheckBox.IsChecked == true)
            {
                AppendConsole("Unexpected exit detected. Restarting in 3 seconds...", ConsoleStream.Launcher);
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (!_closing && !_host.IsRunning) StartServer();
            }
        });

    private void AppendConsole(string text, ConsoleStream stream)
    {
        var timestamped = $"[{DateTime.Now:HH:mm:ss}] {text}";
        ConsoleOutput.Document.Blocks.Add(ConsoleFormatting.CreateParagraph(timestamped, stream));

        var plain = ConsoleFormatting.StripCodes(timestamped);
        _plainLog.AppendLine(plain);
        _sessionLog?.WriteLine(plain);

        while (ConsoleOutput.Document.Blocks.Count > MaximumConsoleParagraphs)
            ConsoleOutput.Document.Blocks.Remove(ConsoleOutput.Document.Blocks.FirstBlock);

        if (AutoScrollCheckBox.IsChecked == true) ConsoleOutput.ScrollToEnd();
    }

    private void SetRunningUi(bool running)
    {
        StartButton.IsEnabled = !running;
        RestartButton.IsEnabled = running;
        StopButton.IsEnabled = running;
        KillButton.IsEnabled = running;
        ExecutablePathBox.IsEnabled = !running;
        ArgumentsBox.IsEnabled = !running;
        WorkingDirectoryBox.IsEnabled = !running;
        ConfigPathBox.IsEnabled = !running;
        PidText.Text = running ? $"PID {_host.ProcessId}" : "PID —";
        StatusText.Text = running ? "RUNNING" : "OFFLINE";
        StatusText.Foreground = running ? Brushes.LightGreen : FindResource("TextBrush") as Brush;
        StatusDot.Fill = running ? Brushes.LimeGreen : Brushes.Gray;
        if (!running) RuntimeText.Text = "00:00:00";
    }

    private void RuntimeTimer_Tick(object? sender, EventArgs e)
    {
        if (_startedAt is null) return;
        RuntimeText.Text = (DateTime.Now - _startedAt.Value).ToString(@"dd\.hh\:mm\:ss");
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e) => FindNext();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        FindNext();
        e.Handled = true;
    }

    private void FindNext()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchResultText.Text = string.Empty;
            return;
        }

        var paragraphs = ConsoleOutput.Document.Blocks.OfType<Paragraph>().ToList();
        var matches = paragraphs.Select((paragraph, index) => new
        {
            Paragraph = paragraph,
            Index = index,
            Text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text
        }).Where(item => item.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            SearchResultText.Text = "No matches";
            return;
        }

        if (!query.Equals(_lastSearch, StringComparison.OrdinalIgnoreCase))
        {
            _lastSearch = query;
            _searchParagraphIndex = -1;
        }

        var next = matches.FirstOrDefault(match => match.Index > _searchParagraphIndex) ?? matches[0];
        _searchParagraphIndex = next.Index;
        ConsoleOutput.Selection.Select(next.Paragraph.ContentStart, next.Paragraph.ContentEnd);
        ConsoleOutput.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromArgb(100, 222, 35, 46)));
        ConsoleOutput.CaretPosition = next.Paragraph.ContentStart;
        ConsoleOutput.ScrollToVerticalOffset(Math.Max(0, ConsoleOutput.VerticalOffset - ConsoleOutput.ViewportHeight / 3));
        SearchResultText.Text = $"{matches.FindIndex(match => match.Index == next.Index) + 1} of {matches.Count}";
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ConsoleOutput.Document.Blocks.Clear();
        _plainLog.Clear();
        _searchParagraphIndex = -1;
        SearchResultText.Text = string.Empty;
    }

    private void SelectAllConsole_Click(object sender, RoutedEventArgs e) => ConsoleOutput.SelectAll();

    private void ExportLogButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            Title = "Export console log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"nairdwood-console-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };

        if (picker.ShowDialog(this) != true) return;
        File.WriteAllText(picker.FileName, _plainLog.ToString());
        FooterStatusText.Text = $"Log exported to {picker.FileName}";
    }

    private void CaptureAndSaveSettings()
    {
        _settings.ExecutablePath = ExecutablePathBox.Text.Trim();
        _settings.Arguments = ArgumentsBox.Text.Trim();
        _settings.WorkingDirectory = WorkingDirectoryBox.Text.Trim();
        _settings.ConfigPath = ConfigPathBox.Text.Trim();
        _settings.RconHost = RconHostBox.Text.Trim();
        _settings.RconPort = int.TryParse(RconPortBox.Text, out var rconPort) ? rconPort : 30120;
        _settings.RconPassword = RconPasswordBox.Password;
        _settings.AutoRestart = AutoRestartCheckBox.IsChecked == true;
        _settings.AutoScroll = AutoScrollCheckBox.IsChecked == true;
        _settings.WindowWidth = ActualWidth;
        _settings.WindowHeight = ActualHeight;
        _settingsService.Save(_settings);
        _settingsResetPending = false;
    }

    private void StartSessionLog()
    {
        CloseSessionLog();
        Directory.CreateDirectory(_settingsService.LogsDirectory);
        var path = Path.Combine(_settingsService.LogsDirectory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _sessionLog = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    private void CloseSessionLog()
    {
        _sessionLog?.Dispose();
        _sessionLog = null;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_host.IsRunning)
        {
            e.Cancel = true;
            if (!_closing)
            {
                ModalOverlay.Visibility = Visibility.Collapsed;
                MariaDbWarningOverlay.Visibility = Visibility.Collapsed;
                SettingsPanel.Visibility = Visibility.Collapsed;
                SetupOverlay.Visibility = Visibility.Collapsed;
                ResetSettingsOverlay.Visibility = Visibility.Collapsed;
                CloseModalOverlay.Visibility = Visibility.Visible;
                CloseConfirmButton.Focus();
            }
            return;
        }

        _closing = true;
        if (_shutdownFinalized) return;
        _shutdownFinalized = true;
        if (!_settingsResetPending)
        {
            try { CaptureAndSaveSettings(); } catch { }
        }
        _runtimeTimer.Stop();
        _mariaDbTimer.Stop();
        CloseSessionLog();
        _host.Dispose();
    }
}
