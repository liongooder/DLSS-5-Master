using System.Text;

namespace DLSS5Master.Core;

/// <summary>
/// Line-based INI reader/editor that leaves everything it was not asked to touch exactly as it was
/// (comments, blank lines, ordering, spacing around '=').
/// </summary>
public static class Ini
{
    // One classified line of the file.
    private enum LineKind { Other, Blank, Comment, Header, Pair }

    private readonly record struct ParsedLine(LineKind Kind, string Section, string Key, int ValueStart);

    public static string? Get(string? text, string section, string key)
    {
        var lines = SplitLines(text, out _);
        var parsed = Classify(lines);
        for (int i = 0; i < lines.Count; i++)
        {
            var p = parsed[i];
            if (p.Kind == LineKind.Pair && Same(p.Section, section) && Same(p.Key, key))
                return lines[i][p.ValueStart..].Trim();
        }
        return null;
    }

    public static string Set(string? text, string section, string key, string value)
    {
        var lines = SplitLines(text, out var newline);
        var parsed = Classify(lines);

        // 1. The key already exists: swap only the value part.
        for (int i = 0; i < lines.Count; i++)
        {
            var p = parsed[i];
            if (p.Kind == LineKind.Pair && Same(p.Section, section) && Same(p.Key, key))
            {
                lines[i] = lines[i][..p.ValueStart] + value;
                return Join(lines, newline);
            }
        }

        // 2. The section exists (the root section always "exists"): insert after its last non-blank line.
        int sectionStart = -1; // index of the header line, or -1 for the root section
        bool found = section.Length == 0;
        if (!found)
        {
            for (int i = 0; i < lines.Count; i++)
                if (parsed[i].Kind == LineKind.Header && Same(parsed[i].Section, section)) { sectionStart = i; found = true; break; }
        }
        if (found)
        {
            int lastContent = sectionStart;
            for (int i = sectionStart + 1; i < lines.Count && parsed[i].Kind != LineKind.Header; i++)
                if (parsed[i].Kind != LineKind.Blank) lastContent = i;
            lines.Insert(lastContent + 1, $"{key}={value}");
            return Join(lines, newline);
        }

        // 3. New section at the end, separated by a single blank line.
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0) lines.Add("");
        lines.Add($"[{section}]");
        lines.Add($"{key}={value}");
        return Join(lines, newline);
    }

    public static List<(string section, string key, string value, string comment)> Entries(string? text)
    {
        var lines = SplitLines(text, out _);
        var parsed = Classify(lines);
        var result = new List<(string, string, string, string)>();
        var pendingComments = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            var p = parsed[i];
            switch (p.Kind)
            {
                case LineKind.Comment:
                    pendingComments.Add(lines[i].TrimStart().TrimStart(';', '#').Trim());
                    break;
                case LineKind.Pair:
                    result.Add((p.Section, p.Key, lines[i][p.ValueStart..].Trim(), string.Join("\n", pendingComments).Trim()));
                    pendingComments.Clear();
                    break;
                default:
                    // Anything else breaks the "directly above" relationship.
                    pendingComments.Clear();
                    break;
            }
        }
        return result;
    }

    public static string ReadText(string file)
    {
        try
        {
            if (!File.Exists(file)) return "";
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ internals

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits into lines without terminators; remembers which newline style the text used.</summary>
    private static List<string> SplitLines(string? text, out string newline)
    {
        text ??= "";
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = new List<string>(text.Split('\n'));
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
        // A terminating newline produces one empty trailing element that is not a real line.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static List<ParsedLine> Classify(List<string> lines)
    {
        var result = new List<ParsedLine>(lines.Count);
        string current = "";
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0) { result.Add(new(LineKind.Blank, current, "", 0)); continue; }
            if (t[0] is ';' or '#') { result.Add(new(LineKind.Comment, current, "", 0)); continue; }
            if (t[0] == '[')
            {
                int close = t.IndexOf(']');
                if (close > 0)
                {
                    current = t[1..close].Trim();
                    result.Add(new(LineKind.Header, current, "", 0));
                    continue;
                }
            }
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Length > 0)
            {
                // The value begins after '=' and any spaces/tabs that follow it, so that prefix is preserved on Set.
                int v = eq + 1;
                while (v < line.Length && (line[v] == ' ' || line[v] == '\t')) v++;
                result.Add(new(LineKind.Pair, current, line[..eq].Trim(), v));
                continue;
            }
            result.Add(new(LineKind.Other, current, "", 0));
        }
        return result;
    }

    private static string Join(List<string> lines, string newline)
    {
        var body = string.Join(newline, lines).TrimEnd('\r', '\n');
        return body + newline;
    }
}
