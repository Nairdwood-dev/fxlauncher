namespace Nairdwood.Launcher.Models;

public sealed class LauncherSettings
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string RconHost { get; set; } = "127.0.0.1";
    public int RconPort { get; set; } = 30120;
    public string RconPassword { get; set; } = string.Empty;
    public bool SetupCompleted { get; set; }
    public bool AutoRestart { get; set; }
    public bool AutoScroll { get; set; } = true;
    public double WindowWidth { get; set; } = 1380;
    public double WindowHeight { get; set; } = 860;
}
