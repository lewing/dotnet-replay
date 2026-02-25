using System.Text.Json;
using Spectre.Console;
using static TextUtils;

class ColorHelper(bool noColor, bool full)
{
    public bool NoColor => noColor;
    public bool Full => full;

    public string Blue(string s) => noColor ? s : $"[blue]{Markup.Escape(s)}[/]";
    public string Green(string s) => noColor ? s : $"[green]{Markup.Escape(s)}[/]";
    public string Yellow(string s) => noColor ? s : $"[yellow]{Markup.Escape(s)}[/]";
    public string Red(string s) => noColor ? s : $"[red]{Markup.Escape(s)}[/]";
    public string Dim(string s) => noColor ? s : $"[dim]{Markup.Escape(s)}[/]";
    public string Bold(string s) => noColor ? s : $"[bold]{Markup.Escape(s)}[/]";
    public string Cyan(string s) => noColor ? s : $"[cyan]{Markup.Escape(s)}[/]";
    public string Separator() => noColor ? new string('-', 60) : $"[dim]{new string('─', Math.Max(40, AnsiConsole.Profile.Width))}[/]";
    public string Truncate(string s, int max) => full ? s : s.Length <= max ? s : s[..max] + $"… [{s.Length - max} more chars]";

    public void WriteMarkupLine(string line)
    {
        if (noColor)
            Console.WriteLine(StripMarkup(line));
        else
        {
            try { AnsiConsole.MarkupLine(line); }
            catch { Console.WriteLine(StripMarkup(line)); }
        }
    }

    public static readonly JsonSerializerOptions SummarySerializer = new() { WriteIndented = true };
    public static readonly JsonSerializerOptions JsonlSerializer = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
