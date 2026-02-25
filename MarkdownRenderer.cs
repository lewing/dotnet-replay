using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Spectre.Console;
using static TextUtils;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

class MarkdownRenderer(bool noColor)
{
    readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    public List<string> RenderLines(string markdown, string colorName, string prefix = "┃ ")
    {
        if (noColor || string.IsNullOrEmpty(markdown))
            return SplitLines(markdown).Select(l => $"{prefix}{l}").ToList();

        var doc = Markdown.Parse(markdown, pipeline);
        List<string> result = [];

        foreach (var block in doc)
        {
            RenderBlock(block, result, colorName, prefix, 0);
        }

        // Filter consecutive blank separator lines (keep at most one between content)
        List<string> filtered = [];
        bool lastWasBlank = false;
        var blankPattern = noColor ? prefix.TrimEnd() : $"[{colorName}]{prefix.TrimEnd()}[/]";

        foreach (var line in result)
        {
            bool isBlank = line.Trim() == blankPattern.Trim() || GetVisibleText(line).Trim() == prefix.TrimEnd();
            if (isBlank && lastWasBlank)
                continue; // Skip consecutive blanks
            filtered.Add(line);
            lastWasBlank = isBlank;
        }

        return filtered;
    }

    void RenderBlock(Block block, List<string> lines, string colorName, string prefix, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var text = RenderInlines(heading.Inline);
                var marker = Markup.Escape(new string('#', heading.Level) + " ");
                // Use compound tag to avoid nesting
                lines.Add(noColor ? $"{prefix}{marker}{text}" : $"[{colorName} bold]{prefix}{marker}{text}[/]");
                lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
                break;
            }
            case ParagraphBlock para:
            {
                var text = RenderInlines(para.Inline);
                foreach (var line in SplitLines(text))
                {
                    // Text from RenderInlines may contain inline markup - wrap at line level
                    if (noColor)
                        lines.Add($"{prefix}{line}");
                    else
                        lines.Add($"[{colorName}]{prefix}{line}[/]");
                }
                lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
                break;
            }
            case FencedCodeBlock fenced:
            {
                var lang = fenced.Info ?? "";
                // Use compound tag for dim styling
                lines.Add(noColor ? $"{prefix}```{Markup.Escape(lang)}" : $"[{colorName} dim]{prefix}```{Markup.Escape(lang)}[/]");
                var codeLines = fenced.Lines;
                for (int i = 0; i < codeLines.Count; i++)
                {
                    var line = codeLines.Lines[i].ToString();
                    lines.Add(noColor ? $"{prefix}  {Markup.Escape(line)}" : $"[{colorName} dim]{prefix}  {Markup.Escape(line)}[/]");
                }
                lines.Add(noColor ? $"{prefix}```" : $"[{colorName} dim]{prefix}```[/]");
                lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
                break;
            }
            case CodeBlock code:
            {
                var codeLines = code.Lines;
                for (int i = 0; i < codeLines.Count; i++)
                {
                    var line = codeLines.Lines[i].ToString();
                    lines.Add(noColor ? $"{prefix}  {Markup.Escape(line)}" : $"[{colorName} dim]{prefix}  {Markup.Escape(line)}[/]");
                }
                lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
                break;
            }
            case ListBlock list:
            {
                int itemNum = 0;
                foreach (var item in list)
                {
                    if (item is ListItemBlock listItem)
                    {
                        itemNum++;
                        var bullet = list.IsOrdered ? $"{itemNum}. " : "• ";
                        var indent = new string(' ', depth * 2);
                        bool first = true;
                        foreach (var sub in listItem)
                        {
                            if (first && sub is ParagraphBlock p)
                            {
                                var text = RenderInlines(p.Inline);
                                foreach (var (line, idx) in SplitLines(text).Select((l, i) => (l, i)))
                                {
                                    if (idx == 0)
                                    {
                                        if (noColor)
                                            lines.Add($"{prefix}{indent}{Markup.Escape(bullet)}{line}");
                                        else
                                            lines.Add($"[{colorName}]{prefix}{indent}{Markup.Escape(bullet)}{line}[/]");
                                    }
                                    else
                                    {
                                        if (noColor)
                                            lines.Add($"{prefix}{indent}{new string(' ', bullet.Length)}{line}");
                                        else
                                            lines.Add($"[{colorName}]{prefix}{indent}{new string(' ', bullet.Length)}{line}[/]");
                                    }
                                }
                                first = false;
                            }
                            else
                            {
                                RenderBlock(sub, lines, colorName, prefix + indent + new string(' ', bullet.Length), depth + 1);
                                first = false;
                            }
                        }
                    }
                }
                lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
                break;
            }
            case ThematicBreakBlock:
            {
                lines.Add(noColor ? $"{prefix}───" : $"[{colorName} dim]{prefix}───[/]");
                break;
            }
            case QuoteBlock quote:
            {
                foreach (var sub in quote)
                {
                    // Quote prefix should not contain markup - handle dim styling in the block rendering
                    var quotePrefix = prefix + "▎ ";
                    RenderBlock(sub, lines, colorName, quotePrefix, depth);
                }
                break;
            }
            case MdTable table:
            {
                // Collect all rows and their cell texts
                var rows = new List<List<string>>();
                foreach (var rowBlock in table)
                {
                    if (rowBlock is MdTableRow row)
                    {
                        var cells = new List<string>();
                        foreach (var cellBlock in row)
                        {
                            if (cellBlock is MdTableCell cell)
                            {
                                var cellText = new StringBuilder();
                                foreach (var sub in cell)
                                {
                                    if (sub is ParagraphBlock p)
                                        cellText.Append(RenderInlines(p.Inline));
                                }
                                cells.Add(cellText.ToString());
                            }
                        }
                        if (cells.Count > 0)
                            rows.Add(cells);
                    }
                }
                if (rows.Count > 0)
                {
                    // Compute column widths
                    int colCount = rows.Max(r => r.Count);
                    var widths = new int[colCount];
                    foreach (var row in rows)
                        for (int c = 0; c < row.Count; c++)
                            widths[c] = Math.Max(widths[c], VisibleWidth(StripMarkup(row[c])));

                    for (int r = 0; r < rows.Count; r++)
                    {
                        var sb = new StringBuilder();
                        for (int c = 0; c < colCount; c++)
                        {
                            if (c > 0) sb.Append(" | ");
                            var cell = c < rows[r].Count ? rows[r][c] : "";
                            var pad = widths[c] - VisibleWidth(StripMarkup(cell));
                            sb.Append(cell);
                            if (pad > 0) sb.Append(new string(' ', pad));
                        }
                        var line = sb.ToString();
                        if (noColor)
                            lines.Add($"{prefix}{line}");
                        else
                            lines.Add($"[{colorName}]{prefix}{line}[/]");

                        // Add separator after header row
                        if (r == 0)
                        {
                            var sep = new StringBuilder();
                            for (int c = 0; c < colCount; c++)
                            {
                                if (c > 0) sep.Append(" | ");
                                sep.Append(new string('-', Math.Max(widths[c], 3)));
                            }
                            var sepLine = sep.ToString();
                            if (noColor)
                                lines.Add($"{prefix}{sepLine}");
                            else
                                lines.Add($"[{colorName} dim]{prefix}{sepLine}[/]");
                        }
                    }
                }
                lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
                break;
            }
            default:
            {
                // Fallback: render raw text for unknown block types
                var rawLines = block.ToString() is string s ? SplitLines(s) : [];
                foreach (var line in rawLines)
                {
                    if (noColor)
                        lines.Add($"{prefix}{Markup.Escape(line)}");
                    else
                        lines.Add($"[{colorName}]{prefix}{Markup.Escape(line)}[/]");
                }
                break;
            }
        }
    }

    string RenderInlines(ContainerInline? container)
    {
        if (container == null) return "";
        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(Markup.Escape(lit.Content.ToString()));
                    break;
                case EmphasisInline em:
                    var inner = RenderInlines(em);
                    if (em.DelimiterCount >= 2)
                        sb.Append(noColor ? inner : $"[bold]{inner}[/]");
                    else
                        sb.Append(noColor ? inner : $"[italic]{inner}[/]");
                    break;
                case CodeInline code:
                    sb.Append(noColor ? code.Content : $"[cyan]{Markup.Escape(code.Content)}[/]");
                    break;
                case LinkInline link:
                    var linkText = RenderInlines(link);
                    var url = link.Url ?? "";
                    if (string.IsNullOrEmpty(linkText)) linkText = Markup.Escape(url);
                    sb.Append(noColor ? $"{linkText} ({url})" : $"[underline blue]{linkText}[/] [dim]({Markup.Escape(url)})[/]");
                    break;
                case LineBreakInline:
                    sb.Append('\n');
                    break;
                case HtmlInline html:
                    sb.Append(Markup.Escape(html.Tag));
                    break;
                default:
                    sb.Append(Markup.Escape(inline.ToString() ?? ""));
                    break;
            }
        }
        return sb.ToString();
    }
}
