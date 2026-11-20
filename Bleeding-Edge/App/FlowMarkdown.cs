using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

// A reading projection. Source offsets let Flow edit and select the original draft.
sealed class FlowMarkdown
{
    static readonly Regex inline = new Regex(
        @"\\(?<escape>[!-/:-@[-\x60{-~])|(?<ticks>\x60+)(?<code>.+?)\k<ticks>(?!\x60)|!?\[(?<label>[^\]\r\n]+)\]\((?:\\.|[^()\r\n]|\([^()\r\n]*\))*\)|(?<mark>\*{1,3}|(?<!\w)_{1,3}|~~)(?!\s|\*|_)(?<inner>.+?)(?<!\s)\k<mark>(?!\*|_)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
    static readonly Regex heading = new Regex(@"^ {0,3}#{1,6}[ \t]+");
    static readonly Regex headingEnd = new Regex(@"[ \t]+#+[ \t]*$");
    static readonly Regex quote = new Regex(@"^ {0,3}(?:>[ \t]?)+");
    static readonly Regex bullet = new Regex(@"^(?<indent>[ \t]*)(?:[-+*])(?=[ \t])");
    static readonly Regex rule = new Regex(@"^ {0,3}(?:(?:\*[ \t]*){3,}|(?:-[ \t]*){3,}|(?:_[ \t]*){3,})$");
    static readonly Regex fence = new Regex(@"^ {0,3}(?<mark>[\x60]{3,}|~{3,})(?<tail>.*)$");
    readonly StringBuilder output = new StringBuilder();
    readonly List<int> offsets = new List<int>();
    readonly List<FontStyle> styles = new List<FontStyle>();
    readonly int sourceLength;
    public readonly string Text;

    public FlowMarkdown(string source)
    {
        sourceLength = source.Length;
        char fenceChar = '\0';
        int fenceSize = 0;
        for (int start = 0; start < source.Length;)
        {
            int lineEnd = start;
            while (lineEnd < source.Length && source[lineEnd] != '\r' && source[lineEnd] != '\n')
                lineEnd++;
            string line = source.Substring(start, lineEnd - start);
            Match blockFence = fence.Match(line);
            var mark = blockFence.Groups["mark"];
            if (fenceSize != 0)
            {
                if (blockFence.Success && mark.Length >= fenceSize && mark.Value[0] == fenceChar &&
                    String.IsNullOrWhiteSpace(blockFence.Groups["tail"].Value))
                    fenceSize = 0;
                else
                    Append(line, start, FontStyle.Regular);
            }
            else if (blockFence.Success)
            {
                fenceChar = mark.Value[0];
                fenceSize = mark.Length;
            }
            else
            {
                Match quoted = quote.Match(line);
                int contentStart = quoted.Length;
                string content = line.Substring(contentStart);
                if (!rule.IsMatch(content))
                {
                    Match title = heading.Match(content);
                    Match list = bullet.Match(content);
                    if (title.Success)
                    {
                        content = headingEnd.Replace(content.Substring(title.Length), "");
                        contentStart += title.Length;
                    }
                    else if (list.Success)
                    {
                        int marker = list.Length - 1;
                        content = content.Substring(0, marker) + "•" + content.Substring(marker + 1);
                    }
                    FormatInline(content, start + contentStart, title.Success ? FontStyle.Bold : FontStyle.Regular, 0);
                }
            }
            if (lineEnd < source.Length)
                Append("\n", lineEnd, FontStyle.Regular);
            start = lineEnd + 1;
            if (start < source.Length && source[lineEnd] == '\r' && source[start] == '\n')
                start++;
        }
        Text = output.ToString();
    }

    void FormatInline(string text, int source, FontStyle style, int depth)
    {
        if (depth >= 12)
        {
            Append(text, source, style);
            return;
        }
        int cursor = 0;
        try
        {
            foreach (Match match in inline.Matches(text))
            {
                Append(text.Substring(cursor, match.Index - cursor), source + cursor, style);
                Group escaped = match.Groups["escape"], code = match.Groups["code"],
                    label = match.Groups["label"], inner = match.Groups["inner"];
                if (escaped.Success)
                    Append(escaped.Value, source + escaped.Index, style);
                else if (code.Success)
                    Append(code.Value, source + code.Index, style);
                else if (label.Success)
                    FormatInline(label.Value, source + label.Index, style, depth + 1);
                else
                {
                    string delimiter = match.Groups["mark"].Value;
                    FontStyle emphasis = delimiter == "~~" ? FontStyle.Strikeout :
                        delimiter.Length == 3 ? FontStyle.Bold | FontStyle.Italic :
                        delimiter.Length == 2 ? FontStyle.Bold : FontStyle.Italic;
                    FormatInline(inner.Value, source + inner.Index, style | emphasis, depth + 1);
                }
                cursor = match.Index + match.Length;
            }
        }
        catch (RegexMatchTimeoutException) { }
        Append(text.Substring(cursor), source + cursor, style);
    }

    void Append(string text, int start, FontStyle style)
    {
        output.Append(text);
        for (int i = 0; i < text.Length; i++)
        {
            offsets.Add(start + i);
            styles.Add(style);
        }
    }

    public int SourceOffset(int index) { return index < offsets.Count ? offsets[index] : sourceLength; }
    public FontStyle StyleAt(int index) { return index < styles.Count ? styles[index] : FontStyle.Regular; }
    public int VisibleOffset(int source)
    {
        int index = offsets.BinarySearch(source);
        return index < 0 ? ~index : index;
    }
}
