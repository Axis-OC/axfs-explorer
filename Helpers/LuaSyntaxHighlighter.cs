using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AxfsExplorer.Helpers;

enum LuaTokenType
{
    Normal,
    Keyword,
    String,
    Comment,
    Number,
    Builtin,
    Punctuation,
}

record LuaToken(string Text, LuaTokenType Type);

static class LuaSyntaxHighlighter
{
    static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and",
        "break",
        "do",
        "else",
        "elseif",
        "end",
        "false",
        "for",
        "function",
        "goto",
        "if",
        "in",
        "local",
        "nil",
        "not",
        "or",
        "repeat",
        "return",
        "then",
        "true",
        "until",
        "while",
    };

    static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "print",
        "require",
        "error",
        "pcall",
        "xpcall",
        "type",
        "tostring",
        "tonumber",
        "pairs",
        "ipairs",
        "next",
        "select",
        "unpack",
        "setmetatable",
        "getmetatable",
        "rawget",
        "rawset",
        "rawequal",
        "table",
        "string",
        "math",
        "io",
        "os",
        "coroutine",
        "debug",
        "bit32",
        "load",
        "dofile",
        "assert",
        "self",
        "rawlen",
    };

    public static List<LuaToken> Tokenize(string code)
    {
        var tokens = new List<LuaToken>();
        int i = 0,
            len = code.Length;
        while (i < len)
        {
            char c = code[i];
            if (c == '\r')
            {
                tokens.Add(new("\n", LuaTokenType.Normal));
                i += (i + 1 < len && code[i + 1] == '\n') ? 2 : 1;
                continue;
            }
            if (c == '\n')
            {
                tokens.Add(new("\n", LuaTokenType.Normal));
                i++;
                continue;
            }

            if (c == '-' && i + 2 < len && code[i + 1] == '-' && code[i + 2] == '[')
            {
                int eq = 0;
                int j = i + 3;
                while (j < len && code[j] == '=')
                {
                    eq++;
                    j++;
                }
                if (j < len && code[j] == '[')
                {
                    string close = "]" + new string('=', eq) + "]";
                    int end = code.IndexOf(close, j + 1, StringComparison.Ordinal);
                    if (end < 0)
                        end = len - close.Length;
                    int fin = Math.Min(end + close.Length, len);
                    tokens.Add(new(code[i..fin], LuaTokenType.Comment));
                    i = fin;
                    continue;
                }
            }

            if (c == '-' && i + 1 < len && code[i + 1] == '-')
            {
                int end = code.IndexOf('\n', i);
                if (end < 0)
                    end = len;
                tokens.Add(new(code[i..end], LuaTokenType.Comment));
                i = end;
                continue;
            }

            if (c == '[')
            {
                int eq = 0;
                int j = i + 1;
                while (j < len && code[j] == '=')
                {
                    eq++;
                    j++;
                }
                if (j < len && code[j] == '[')
                {
                    string close = "]" + new string('=', eq) + "]";
                    int end = code.IndexOf(close, j + 1, StringComparison.Ordinal);
                    if (end < 0)
                        end = len - close.Length;
                    int fin = Math.Min(end + close.Length, len);
                    tokens.Add(new(code[i..fin], LuaTokenType.String));
                    i = fin;
                    continue;
                }
            }

            if (c == '"' || c == '\'')
            {
                var sb = new StringBuilder();
                sb.Append(c);
                i++;
                while (i < len && code[i] != c && code[i] != '\n')
                {
                    if (code[i] == '\\' && i + 1 < len)
                    {
                        sb.Append(code[i]);
                        sb.Append(code[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(code[i]);
                        i++;
                    }
                }
                if (i < len && code[i] == c)
                {
                    sb.Append(c);
                    i++;
                }
                tokens.Add(new(sb.ToString(), LuaTokenType.String));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(code[i + 1])))
            {
                var sb = new StringBuilder();
                if (c == '0' && i + 1 < len && (code[i + 1] | 0x20) == 'x')
                {
                    sb.Append(code[i]);
                    sb.Append(code[i + 1]);
                    i += 2;
                    while (i < len && char.IsAsciiHexDigit(code[i]))
                    {
                        sb.Append(code[i]);
                        i++;
                    }
                }
                else
                {
                    while (i < len && (char.IsDigit(code[i]) || code[i] == '.'))
                    {
                        sb.Append(code[i]);
                        i++;
                    }
                    if (i < len && (code[i] | 0x20) == 'e')
                    {
                        sb.Append(code[i]);
                        i++;
                        if (i < len && (code[i] == '+' || code[i] == '-'))
                        {
                            sb.Append(code[i]);
                            i++;
                        }
                        while (i < len && char.IsDigit(code[i]))
                        {
                            sb.Append(code[i]);
                            i++;
                        }
                    }
                }
                tokens.Add(new(sb.ToString(), LuaTokenType.Number));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var sb = new StringBuilder();
                while (i < len && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                {
                    sb.Append(code[i]);
                    i++;
                }
                string w = sb.ToString();
                tokens.Add(
                    new(
                        w,
                        Keywords.Contains(w) ? LuaTokenType.Keyword
                            : Builtins.Contains(w) ? LuaTokenType.Builtin
                            : LuaTokenType.Normal
                    )
                );
                continue;
            }

            if (c == ' ' || c == '\t')
            {
                var sb = new StringBuilder();
                while (i < len && (code[i] == ' ' || code[i] == '\t'))
                {
                    sb.Append(code[i]);
                    i++;
                }
                tokens.Add(new(sb.ToString(), LuaTokenType.Normal));
                continue;
            }

            tokens.Add(new(c.ToString(), LuaTokenType.Punctuation));
            i++;
        }
        return tokens;
    }

    public static SolidColorBrush Brush(LuaTokenType t, bool dk) =>
        new(
            t switch
            {
                LuaTokenType.Keyword => dk
                    ? ColorHelper.FromArgb(255, 86, 156, 214)
                    : ColorHelper.FromArgb(255, 0, 0, 255),
                LuaTokenType.String => dk
                    ? ColorHelper.FromArgb(255, 206, 145, 120)
                    : ColorHelper.FromArgb(255, 163, 21, 21),
                LuaTokenType.Comment => dk
                    ? ColorHelper.FromArgb(255, 106, 153, 85)
                    : ColorHelper.FromArgb(255, 0, 128, 0),
                LuaTokenType.Number => dk
                    ? ColorHelper.FromArgb(255, 181, 206, 168)
                    : ColorHelper.FromArgb(255, 9, 134, 88),
                LuaTokenType.Builtin => dk
                    ? ColorHelper.FromArgb(255, 220, 220, 168)
                    : ColorHelper.FromArgb(255, 43, 145, 175),
                LuaTokenType.Punctuation => dk
                    ? ColorHelper.FromArgb(255, 150, 150, 150)
                    : ColorHelper.FromArgb(255, 100, 100, 100),
                _ => dk
                    ? ColorHelper.FromArgb(255, 212, 212, 212)
                    : ColorHelper.FromArgb(255, 30, 30, 30),
            }
        );

    // ── FIX: Split tokens into lines as a separate step ──
    static List<List<LuaToken>> SplitIntoLines(List<LuaToken> tokens)
    {
        var lines = new List<List<LuaToken>> { new() };
        foreach (var tok in tokens)
        {
            if (tok.Text == "\n")
            {
                lines.Add(new());
                continue;
            }
            string text = tok.Text;
            int start = 0;
            for (int j = 0; j < text.Length; j++)
            {
                if (text[j] == '\n')
                {
                    if (j > start)
                        lines[^1].Add(new(text[start..j], tok.Type));
                    lines.Add(new());
                    start = j + 1;
                }
            }
            if (start < text.Length)
                lines[^1].Add(new(text[start..], tok.Type));
        }
        return lines;
    }

    /// <summary>
    /// FIX: Uses a SINGLE Paragraph with LineBreak elements instead of one
    /// Paragraph per line. This reduces layout passes from O(N) to O(1)
    /// and fixes the extreme CPU cost / scroll lag for large files.
    /// Default maxLines lowered to 300 (was 5000).
    /// </summary>
    public static RichTextBlock Highlight(
        string code,
        bool isDark,
        bool lineNums = true,
        int fontSize = 13,
        int maxLines = 300
    )
    {
        var rtb = new RichTextBlock
        {
            FontFamily = new FontFamily("Cascadia Code,Consolas"),
            FontSize = fontSize,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
        };

        var lines = SplitIntoLines(Tokenize(code));
        int total = Math.Min(lines.Count, maxLines);
        int numW = Math.Max(3, total.ToString().Length);
        var numBrush = new SolidColorBrush(
            isDark ? ColorHelper.FromArgb(60, 200, 200, 200) : ColorHelper.FromArgb(50, 0, 0, 0)
        );

        // ── Single-paragraph approach: dramatically faster layout ──
        var para = new Paragraph
        {
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = fontSize + 7,
        };

        for (int i = 0; i < total; i++)
        {
            if (i > 0)
                para.Inlines.Add(new LineBreak());

            if (lineNums)
                para.Inlines.Add(
                    new Run
                    {
                        Text = $"{(i + 1).ToString().PadLeft(numW)}  ",
                        Foreground = numBrush,
                    }
                );

            if (lines[i].Count == 0)
                para.Inlines.Add(new Run { Text = " " });
            else
                foreach (var tok in lines[i])
                    para.Inlines.Add(
                        new Run { Text = tok.Text, Foreground = Brush(tok.Type, isDark) }
                    );
        }

        rtb.Blocks.Add(para);

        if (lines.Count > maxLines)
        {
            var overflow = new Paragraph();
            overflow.Inlines.Add(
                new Run
                {
                    Text =
                        $"\n… {lines.Count - maxLines:N0} more lines (use Edit tab for full file)",
                    Foreground = numBrush,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                }
            );
            rtb.Blocks.Add(overflow);
        }
        return rtb;
    }

    public static bool IsLuaFile(string name) =>
        Path.GetExtension(name).Equals(".lua", StringComparison.OrdinalIgnoreCase);

    public static bool IsTextExt(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext
            is ".lua"
                or ".cfg"
                or ".txt"
                or ".log"
                or ".vbl"
                or ".sig"
                or ".json"
                or ".xml"
                or ".md"
                or ".ini"
                or ".toml"
                or ".yaml"
                or ".yml"
                or "";
    }
}
