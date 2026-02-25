using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

static class TextUtils
{
    public static string StripMarkup(string s)
    {
        // Preserve escaped brackets
        s = s.Replace("[[", "\x01").Replace("]]", "\x02");
        // Strip all markup tags
        s = Regex.Replace(s, @"\[[^\[\]]*\]", "");
        // Restore escaped brackets to their visible form
        s = s.Replace("\x01", "[").Replace("\x02", "]");
        return s;
    }

    public static string GetVisibleText(string s) => StripMarkup(s);

    public static string[] SplitLines(string s) => s.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

    public static bool IsWideEmojiInBMP(int value) => value switch
    {
        0x231A or 0x231B => true, // ⌚⌛
        0x23E9 or 0x23EA or 0x23EB or 0x23EC or 0x23F0 or 0x23F3 => true,
        >= 0x25AA and <= 0x25AB => true,
        0x25B6 or 0x25C0 => true,
        >= 0x25FB and <= 0x25FE => true,
        >= 0x2600 and <= 0x2604 => true,
        0x260E or 0x2611 => true,
        >= 0x2614 and <= 0x2615 => true,
        0x2618 or 0x261D or 0x2620 => true,
        >= 0x2622 and <= 0x2623 => true,
        0x2626 or 0x262A or 0x262E or 0x262F => true,
        >= 0x2638 and <= 0x263A => true,
        0x2640 or 0x2642 => true,
        >= 0x2648 and <= 0x2653 => true, // zodiac
        0x265F or 0x2660 or 0x2663 or 0x2665 or 0x2666 => true,
        0x2668 or 0x267B or 0x267E or 0x267F => true,
        >= 0x2692 and <= 0x2697 => true,
        0x2699 or 0x269B or 0x269C => true,
        >= 0x26A0 and <= 0x26A1 => true,
        >= 0x26AA and <= 0x26AB => true,
        >= 0x26B0 and <= 0x26B1 => true,
        >= 0x26BD and <= 0x26BE => true,
        >= 0x26C4 and <= 0x26C5 => true,
        0x26C8 or 0x26CE or 0x26CF => true,
        0x26D1 or 0x26D3 or 0x26D4 => true,
        0x26E9 or 0x26EA => true,
        >= 0x26F0 and <= 0x26F5 => true,
        >= 0x26F7 and <= 0x26FA => true,
        0x26FD => true,
        0x2702 or 0x2705 => true,
        >= 0x2708 and <= 0x270D => true,
        0x270F => true,
        0x2712 or 0x2714 or 0x2716 => true,
        0x271D or 0x2721 => true,
        0x2728 => true,
        0x2733 or 0x2734 => true,
        0x2744 or 0x2747 => true,
        0x274C or 0x274E => true,
        >= 0x2753 and <= 0x2755 => true,
        0x2757 => true,
        >= 0x2763 and <= 0x2764 => true,
        >= 0x2795 and <= 0x2797 => true,
        0x27A1 or 0x27B0 or 0x27BF => true,
        >= 0x2934 and <= 0x2935 => true,
        >= 0x2B05 and <= 0x2B07 => true,
        0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 => true,
        _ => false
    };

    public static int RuneWidth(Rune rune)
    {
        int v = rune.Value;
        // Zero-width: variation selectors and combining marks
        if (v == 0xFE0F || v == 0xFE0E || (v >= 0x200B && v <= 0x200F) || v == 0x2060 || v == 0xFEFF)
            return 0;
        // Wide: CJK, fullwidth, emoji
        if (v >= 0x1100 && (
            (v <= 0x115F) ||                          // Hangul Jamo
            (v >= 0x2E80 && v <= 0x9FFF) ||            // CJK
            (v >= 0xF900 && v <= 0xFAFF) ||            // CJK Compatibility
            (v >= 0xFE30 && v <= 0xFE6F) ||            // CJK Compatibility Forms
            (v >= 0xFF01 && v <= 0xFF60) ||             // Fullwidth forms
            (v >= 0x1F000)))                            // Supplementary emoji (🔧💭 etc.)
            return 2;
        // BMP emoji with default emoji presentation
        if (IsWideEmojiInBMP(v))
            return 2;
        return 1;
    }

    public static int VisibleWidth(string s)
    {
        int width = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }
        return width;
    }

    public static string TruncateToWidth(string s, int maxWidth)
    {
        int width = 0;
        int i = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            int charWidth = RuneWidth(rune);
            if (width + charWidth > maxWidth) break;
            width += charWidth;
            i += rune.Utf16SequenceLength;
        }
        return s[..i];
    }

    public static string TruncateMarkupToWidth(string markupText, int maxWidth)
    {
        var result = new StringBuilder();
        var openTags = new Stack<string>();
        int visWidth = 0;
        int i = 0;
        while (i < markupText.Length && visWidth < maxWidth)
        {
            // Check for escaped brackets [[ or ]]
            if (i + 1 < markupText.Length && markupText[i] == '[' && markupText[i + 1] == '[')
            {
                if (visWidth + 1 > maxWidth) break;
                result.Append("[[");
                visWidth++;
                i += 2;
                continue;
            }
            if (i + 1 < markupText.Length && markupText[i] == ']' && markupText[i + 1] == ']')
            {
                if (visWidth + 1 > maxWidth) break;
                result.Append("]]");
                visWidth++;
                i += 2;
                continue;
            }
            // Check for markup tags [xxx] or [/xxx] or [/]
            if (markupText[i] == '[')
            {
                int closeIdx = markupText.IndexOf(']', i + 1);
                if (closeIdx > i)
                {
                    var tag = markupText[(i + 1)..closeIdx];
                    result.Append(markupText[i..(closeIdx + 1)]);
                    if (tag == "/" || tag.StartsWith("/"))
                    {
                        if (openTags.Count > 0) openTags.Pop();
                    }
                    else
                    {
                        openTags.Push(tag);
                    }
                    i = closeIdx + 1;
                    continue;
                }
            }
            // Regular character — check width
            try
            {
                var rune = Rune.GetRuneAt(markupText, i);
                int charWidth = RuneWidth(rune);
                if (visWidth + charWidth > maxWidth) break;
                result.Append(markupText.AsSpan(i, rune.Utf16SequenceLength));
                visWidth += charWidth;
                i += rune.Utf16SequenceLength;
            }
            catch
            {
                // Invalid surrogate pair — treat as single-width character
                if (visWidth + 1 > maxWidth) break;
                result.Append(markupText[i]);
                visWidth++;
                i++;
            }
        }
        // Append ellipsis inside the current markup context if truncated, then close open tags
        bool wasTruncated = i < markupText.Length;
        if (wasTruncated) result.Append('…');
        while (openTags.Count > 0)
        {
            openTags.Pop();
            result.Append("[/]");
        }
        return result.ToString();
    }

    public static string SkipMarkupWidth(string markupText, int skipColumns)
    {
        if (skipColumns <= 0) return markupText;
        List<string> openTags = [];
        int visWidth = 0;
        int i = 0;
        while (i < markupText.Length && visWidth < skipColumns)
        {
            // Escaped brackets [[ or ]]
            if (i + 1 < markupText.Length && markupText[i] == '[' && markupText[i + 1] == '[')
            {
                visWidth++;
                i += 2;
                continue;
            }
            if (i + 1 < markupText.Length && markupText[i] == ']' && markupText[i + 1] == ']')
            {
                visWidth++;
                i += 2;
                continue;
            }
            // Markup tags
            if (markupText[i] == '[')
            {
                int closeIdx = markupText.IndexOf(']', i + 1);
                if (closeIdx > i)
                {
                    var tag = markupText[(i + 1)..closeIdx];
                    if (tag == "/" || tag.StartsWith("/"))
                    {
                        if (openTags.Count > 0) openTags.RemoveAt(openTags.Count - 1);
                    }
                    else
                    {
                        openTags.Add(tag);
                    }
                    i = closeIdx + 1;
                    continue;
                }
            }
            // Regular character
            try
            {
                var rune = Rune.GetRuneAt(markupText, i);
                int charWidth = RuneWidth(rune);
                visWidth += charWidth;
                i += rune.Utf16SequenceLength;
            }
            catch
            {
                visWidth++;
                i++;
            }
        }
        // Re-open any tags that were active at the skip point
        var prefix = new StringBuilder();
        foreach (var tag in openTags)
            prefix.Append($"[{tag}]");
        return prefix.ToString() + markupText[i..];
    }

    public static string PadVisible(string s, int totalWidth)
    {
        var visible = GetVisibleText(s);
        int padding = totalWidth - VisibleWidth(visible);
        return padding > 0 ? s + new string(' ', padding) : s;
    }

    public static string FormatRelativeTime(TimeSpan ts) => ts switch
    {
        { TotalSeconds: < 0.1 } => "+0.0s",
        { TotalMinutes: < 1 } => $"+{ts.TotalSeconds:F1}s",
        { TotalHours: < 1 } => $"+{(int)ts.TotalMinutes}m {ts.Seconds}s",
        _ => $"+{(int)ts.TotalHours}h {ts.Minutes}m"
    };

    public static string ExtractContentString(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? "";
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString() ?? "";
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString() ?? "";
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString() ?? "";
            }
        }
        return el.GetRawText();
    }

    public static string SafeGetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    public static string FormatAge(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "now",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)age.TotalHours}h",
        { TotalDays: < 30 } => $"{(int)age.TotalDays}d",
        _ => $"{(int)(age.TotalDays / 30)}mo"
    };

    public static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024}KB",
        _ => $"{bytes / (1024 * 1024.0):F1}MB"
    };

    public static string FormatDuration(TimeSpan ts) => ts switch
    {
        { TotalSeconds: < 60 } => $"{(int)ts.TotalSeconds}s",
        { TotalMinutes: < 60 } => $"{(int)ts.TotalMinutes}m {ts.Seconds}s",
        _ => $"{(int)ts.TotalHours}h {ts.Minutes}m"
    };

    public static List<string> ExpandGlob(string pattern)
    {
        List<string> result = [];

        // Check if pattern contains wildcards
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            // No wildcards, treat as literal path
            if (File.Exists(pattern))
                result.Add(Path.GetFullPath(pattern));
            return result;
        }

        // Split pattern into directory and filename parts
        var dirPath = Path.GetDirectoryName(pattern);
        var fileName = Path.GetFileName(pattern);

        if (string.IsNullOrEmpty(dirPath))
            dirPath = ".";

        if (!Directory.Exists(dirPath))
            return result;

        try
        {
            var files = Directory.GetFiles(dirPath, fileName, SearchOption.TopDirectoryOnly);
            result.AddRange(files.Select(Path.GetFullPath));
        }
        catch
        {
            // Ignore errors (e.g., access denied)
        }

        return result;
    }
}
