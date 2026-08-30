using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Nairdwood.Launcher.Services;

public static partial class ConsoleFormatting
{
    private static readonly Brush DefaultBrush = new SolidColorBrush(Color.FromRgb(218, 223, 232));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));

    public static Paragraph CreateParagraph(string source, ConsoleStream stream)
    {
        source = StripTerminalControls(source);
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = 19,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5
        };

        var currentBrush = stream == ConsoleStream.Error ? ErrorBrush : DefaultBrush;
        var currentWeight = FontWeights.Normal;
        var cursor = 0;

        foreach (Match match in ColourTokenRegex().Matches(source))
        {
            if (match.Index > cursor)
                paragraph.Inlines.Add(new Run(source[cursor..match.Index]) { Foreground = currentBrush, FontWeight = currentWeight });

            ApplyToken(match.Value, stream, ref currentBrush, ref currentWeight);
            cursor = match.Index + match.Length;
        }

        if (cursor < source.Length)
            paragraph.Inlines.Add(new Run(source[cursor..]) { Foreground = currentBrush, FontWeight = currentWeight });

        if (paragraph.Inlines.Count == 0) paragraph.Inlines.Add(new Run(" "));
        return paragraph;
    }

    public static string StripCodes(string source) => ColourTokenRegex().Replace(StripTerminalControls(source), string.Empty);

    private static string StripTerminalControls(string source)
    {
        // txAdmin frequently sets the Windows terminal title with OSC 0. Redirected output does
        // not consume it, so several updates can accumulate until the next real newline. They are
        // terminal instructions rather than log content and must never be rendered or persisted.
        source = OscSequenceRegex().Replace(source, string.Empty);
        source = NonColourCsiRegex().Replace(source, string.Empty);
        return source.Replace("\a", string.Empty);
    }

    private static void ApplyToken(string token, ConsoleStream stream, ref Brush brush, ref FontWeight weight)
    {
        if (token.StartsWith('^'))
        {
            brush = token.Length > 1 ? CaretColour(token[1], stream) : DefaultBrush;
            weight = FontWeights.Normal;
            return;
        }

        var body = token[2..^1];
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var code)) continue;
            if (code == 0)
            {
                brush = stream == ConsoleStream.Error ? ErrorBrush : DefaultBrush;
                weight = FontWeights.Normal;
            }
            else if (code == 1) weight = FontWeights.SemiBold;
            else if (AnsiBrush(code) is { } ansiBrush) brush = ansiBrush;
        }
    }

    private static Brush CaretColour(char code, ConsoleStream stream) => code switch
    {
        '0' => stream == ConsoleStream.Error ? ErrorBrush : DefaultBrush,
        '1' => Brushes.IndianRed,
        '2' => Brushes.LightGreen,
        '3' => Brushes.Gold,
        '4' => Brushes.CornflowerBlue,
        '5' => Brushes.LightSkyBlue,
        '6' => Brushes.Plum,
        '7' => Brushes.WhiteSmoke,
        '8' => Brushes.DarkGray,
        '9' => Brushes.DodgerBlue,
        _ => DefaultBrush
    };

    private static Brush? AnsiBrush(int code) => code switch
    {
        30 => Brushes.Gray,
        31 or 91 => Brushes.IndianRed,
        32 or 92 => Brushes.LightGreen,
        33 or 93 => Brushes.Gold,
        34 or 94 => Brushes.CornflowerBlue,
        35 or 95 => Brushes.Plum,
        36 or 96 => Brushes.LightSkyBlue,
        37 or 97 => Brushes.WhiteSmoke,
        90 => Brushes.DarkGray,
        _ => null
    };

    [GeneratedRegex("(?:\\x1B\\[[0-9;]*m|\\^[0-9])")]
    private static partial Regex ColourTokenRegex();

    [GeneratedRegex("(?:\\x1B)?\\][^\\x07]*(?:\\x07|\\x1B\\\\)")]
    private static partial Regex OscSequenceRegex();

    [GeneratedRegex("\\x1B\\[(?![0-9;]*m)[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex NonColourCsiRegex();
}
