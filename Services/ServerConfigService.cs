using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Nairdwood.Launcher.Services;

public static partial class ServerConfigService
{
    [GeneratedRegex(@"(?im)^[\t ]*(?:set|setr)[\t ]+rcon_password[\t ]+(?:""([^""]*)""|'([^']*)'|([^\s#;]+))[\t ]*(?:[#;].*)?$")]
    private static partial Regex RconLineRegex();

    public static string? ReadRconPassword(string path)
    {
        ValidatePath(path);
        var match = RconLineRegex().Match(File.ReadAllText(path));
        if (!match.Success) return null;
        return match.Groups[1].Success ? match.Groups[1].Value
            : match.Groups[2].Success ? match.Groups[2].Value
            : match.Groups[3].Value;
    }

    public static void WriteRconPassword(string path, string password)
    {
        ValidatePath(path);
        password = password.Trim();
        if (password.Length < 8)
            throw new InvalidOperationException("Use an RCON password with at least 8 characters.");
        if (password.IndexOfAny(new[] { '\r', '\n', '\"', '\'' }) >= 0)
            throw new InvalidOperationException("RCON passwords cannot contain quotes or line breaks.");

        var original = File.ReadAllText(path);
        var replacement = $"set rcon_password \"{password}\"";
        var updated = RconLineRegex().IsMatch(original)
            ? RconLineRegex().Replace(original, replacement)
            : original.TrimEnd('\r', '\n') + Environment.NewLine + Environment.NewLine + replacement + Environment.NewLine;

        if (updated == original) return;
        var backupPath = path + ".nairdwood-backup";
        if (!File.Exists(backupPath)) File.Copy(path, backupPath);
        File.WriteAllText(path, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Select an existing server configuration file first.", path);
    }
}
