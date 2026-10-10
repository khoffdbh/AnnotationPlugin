using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// What a review shows, read and laid out by the plugin itself. The window's diff viewer and code editor are not open to plugins,
// and an HTML fragment runs no script, so the review canvas draws the diff and the plan on its own: this file reads `git diff`
// into files, hunks and numbered lines, and splits a plan into Markdown blocks with their line ranges, for ui/review.js to render.

// ---------------------------------------------------------------- Loading

public static class ReviewSource
{
    // The content of a review: the uncommitted diff, or the plan named by a `plan:<relative path>` key.
    public static async Task<ReviewContent> LoadAsync(string root, string review, CancellationToken cancellationToken)
    {
        if (review.StartsWith(AnnotatePlugin.PlanPrefix, StringComparison.Ordinal))
        {
            var relative = review[AnnotatePlugin.PlanPrefix.Length..];
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return ReviewContent.Failed("plan", relative, "The plan is outside the project.");
            if (!File.Exists(full)) return ReviewContent.Failed("plan", relative, $"The plan `{relative}` does not exist.");
            var lines = DiffSource.SplitLines(File.ReadAllText(full));
            return new ReviewContent("plan", relative, null, [], PlanSplitter.Split(lines), (file, _) => file == relative ? DiffSource.SplitLines(File.ReadAllText(full)) : null);
        }

        return await DiffSource.LoadAsync(root, cancellationToken);
    }
}

// ---------------------------------------------------------------- Diff

public sealed record DiffLine(string K, int? O, int? N, string T);

public sealed record DiffHunk(string Header, IReadOnlyList<DiffLine> Lines);

public sealed record DiffFile(string Path, string? OldPath, string Status, bool Binary, bool TooLarge, int Additions, int Deletions, IReadOnlyList<DiffHunk> Hunks);

public sealed record ReviewContent(string Kind, string? PlanPath, string? Error, IReadOnlyList<DiffFile> Files, IReadOnlyList<PlanBlock> Blocks, Func<string, string, IReadOnlyList<string>?> LinesOf)
{
    public static ReviewContent Failed(string kind, string? planPath, string error) => new(kind, planPath, error, [], [], (_, _) => null);
}

public static class DiffSource
{
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const int MaxFileLines = 3000;
    private const int MaxFileBytes = 512 * 1024;
    private const int Budget = 700_000;

    // The uncommitted state against HEAD (staged and not), plus the untracked files shown as additions.
    public static async Task<ReviewContent> LoadAsync(string root, CancellationToken cancellationToken)
    {
        var inside = await GitAsync(root, cancellationToken, "rev-parse", "--is-inside-work-tree");
        if (inside.Exit != 0) return ReviewContent.Failed("diff", null, "The project is not a git repository.");
        var hasHead = (await GitAsync(root, cancellationToken, "rev-parse", "--verify", "-q", "HEAD")).Exit == 0;
        var diff = await GitAsync(root, cancellationToken, "-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "-U3", hasHead ? "HEAD" : EmptyTree, "--");
        if (diff.Exit != 0) return ReviewContent.Failed("diff", null, "git diff failed: " + diff.Err.Trim());

        var files = DiffParser.Parse(diff.Out);
        var untracked = await GitAsync(root, cancellationToken, "-c", "core.quotepath=false", "ls-files", "--others", "--exclude-standard", "-z");
        foreach (var path in untracked.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries)) files.Add(Untracked(root, path));
        files = Limit(files);

        var oldCache = new ConcurrentDictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
        IReadOnlyList<string>? LinesOf(string file, string side)
        {
            if (side == "old")
                return hasHead ? oldCache.GetOrAdd(file, path => Show(root, path)) : null;
            var full = Path.Combine(root, file);
            return File.Exists(full) ? SplitLines(File.ReadAllText(full)) : null;
        }

        return new ReviewContent("diff", null, null, files, [], LinesOf);
    }

    private static IReadOnlyList<string>? Show(string root, string path)
    {
        var shown = GitAsync(root, CancellationToken.None, "show", "HEAD:" + path).GetAwaiter().GetResult();
        return shown.Exit == 0 ? SplitLines(shown.Out) : null;
    }

    internal static List<string> SplitLines(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static DiffFile Untracked(string root, string path)
    {
        var full = Path.Combine(root, path);
        var info = new FileInfo(full);
        if (!info.Exists) return new DiffFile(path, null, "untracked", false, false, 0, 0, []);
        if (info.Length > MaxFileBytes) return new DiffFile(path, null, "untracked", false, true, 0, 0, []);
        var bytes = File.ReadAllBytes(full);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0) return new DiffFile(path, null, "untracked", true, false, 0, 0, []);
        var lines = SplitLines(Encoding.UTF8.GetString(bytes));
        var hunk = new DiffHunk($"@@ -0,0 +1,{lines.Count} @@", [.. lines.Select((text, index) => new DiffLine("+", null, index + 1, text))]);
        return new DiffFile(path, null, "untracked", false, false, lines.Count, 0, lines.Count == 0 ? [] : [hunk]);
    }

    // An RPC frame is at most 1 MiB: very long files, and files past the budget, are listed without their lines.
    private static List<DiffFile> Limit(List<DiffFile> files)
    {
        var used = 0;
        var limited = new List<DiffFile>(files.Count);
        foreach (var file in files)
        {
            var count = file.Hunks.Sum(hunk => hunk.Lines.Count);
            var size = file.Hunks.Sum(hunk => hunk.Lines.Sum(line => line.T.Length + 32));
            if (count > MaxFileLines || used + size > Budget) { limited.Add(file with { TooLarge = true, Hunks = [] }); continue; }
            used += size;
            limited.Add(file);
        }

        return limited;
    }

    internal static async Task<(int Exit, string Out, string Err)> GitAsync(string root, CancellationToken cancellationToken, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        using var registration = cancellationToken.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }
}

// Reads `git diff` output: files, hunks and lines with their old and new numbers.
public static class DiffParser
{
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

    public static List<DiffFile> Parse(string text)
    {
        var files = new List<DiffFile>();
        string? path = null, oldPath = null, status = null;
        var binary = false;
        List<DiffHunk>? hunks = null;
        List<DiffLine>? lines = null;
        string header = "";
        int oldNo = 0, newNo = 0;

        void EndHunk()
        {
            if (lines is not null) hunks!.Add(new DiffHunk(header, lines));
            lines = null;
        }

        void EndFile()
        {
            EndHunk();
            if (path is null || hunks is null) return;
            var all = hunks.SelectMany(hunk => hunk.Lines).ToList();
            files.Add(new DiffFile(path, oldPath != path ? oldPath : null, status ?? "modified", binary, false, all.Count(line => line.K == "+"), all.Count(line => line.K == "-"), hunks));
            path = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                EndFile();
                (oldPath, path) = GitHeaderPaths(line);
                status = null;
                binary = false;
                hunks = [];
                continue;
            }

            if (hunks is null) continue;
            var hunk = HunkHeader.Match(line);
            if (hunk.Success)
            {
                EndHunk();
                header = line;
                oldNo = int.Parse(hunk.Groups[1].Value);
                newNo = int.Parse(hunk.Groups[3].Value);
                lines = [];
                continue;
            }

            if (lines is null)
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal)) status = "added";
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) status = "deleted";
                else if (line.StartsWith("rename from ", StringComparison.Ordinal)) { oldPath = Unquote(line[12..]); status = "renamed"; }
                else if (line.StartsWith("rename to ", StringComparison.Ordinal)) path = Unquote(line[10..]);
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal)) binary = true;
                else if (line.StartsWith("--- ", StringComparison.Ordinal) && StripPrefix(line[4..]) is { } from) oldPath = from;
                else if (line.StartsWith("+++ ", StringComparison.Ordinal) && StripPrefix(line[4..]) is { } to) path = to;
                continue;
            }

            if (line.Length == 0) continue;
            switch (line[0])
            {
                case ' ': lines.Add(new DiffLine(" ", oldNo++, newNo++, line[1..])); break;
                case '+': lines.Add(new DiffLine("+", null, newNo++, line[1..])); break;
                case '-': lines.Add(new DiffLine("-", oldNo++, null, line[1..])); break;
            }
        }

        EndFile();
        return files;
    }

    // `diff --git a/x b/x`: the two paths, used until the --- and +++ lines say better (binary files and pure renames have none).
    private static (string? Old, string? New) GitHeaderPaths(string line)
    {
        var rest = line["diff --git ".Length..];
        if (rest.StartsWith('"'))
        {
            var parts = Regex.Matches(rest, "\"(?:[^\"\\\\]|\\\\.)*\"|\\S+").Select(match => match.Value).ToList();
            return parts.Count >= 2 ? (StripPrefix(parts[0]), StripPrefix(parts[1])) : (null, null);
        }

        var middle = rest.Length / 2;
        if (rest.Length % 2 == 1 && rest[middle] == ' ' && rest[..middle][2..] == rest[(middle + 1)..][2..]) return (StripPrefix(rest[..middle]), StripPrefix(rest[(middle + 1)..]));
        var split = rest.LastIndexOf(" b/", StringComparison.Ordinal);
        return split > 0 ? (StripPrefix(rest[..split]), StripPrefix(rest[(split + 1)..])) : (null, null);
    }

    private static string? StripPrefix(string value)
    {
        value = Unquote(value.TrimEnd('\t'));
        if (value == "/dev/null") return null;
        return value.Length > 2 && (value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)) ? value[2..] : value;
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        var bytes = new List<byte>();
        for (var index = 1; index < value.Length - 1; index++)
        {
            var c = value[index];
            if (c != '\\') { bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString())); continue; }
            var next = value[++index];
            if (next is >= '0' and <= '7' && index + 2 < value.Length - 1)
            {
                bytes.Add(Convert.ToByte(value.Substring(index, 3), 8));
                index += 2;
                continue;
            }

            bytes.Add((byte)(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next }));
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}

// ---------------------------------------------------------------- Plan

public sealed record PlanBlock(int Start, int End, string Kind, string Text, string? Section);

// Splits a Markdown file into blocks (front matter, heading, paragraph, list item, fenced code, table, quote) with their 1-based line range.
public static class PlanSplitter
{
    private static readonly Regex Heading = new(@"^#{1,6}\s", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^\s*(`{3,}|~{3,})", RegexOptions.Compiled);
    private static readonly Regex Item = new(@"^\s*([-*+]|\d+[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex Table = new(@"^\s*\|", RegexOptions.Compiled);
    private static readonly Regex Quote = new(@"^\s*>", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);

    public static IReadOnlyList<PlanBlock> Split(IReadOnlyList<string> lines)
    {
        var blocks = new List<PlanBlock>();
        string? section = null;
        var index = 0;

        void Add(int from, int to, string kind)
        {
            var slice = lines.Skip(from).Take(to - from + 1).ToList();
            blocks.Add(new PlanBlock(from + 1, to + 1, kind, kind is "code" or "frontmatter" ? string.Join('\n', slice) : Dedent(slice), section));
        }

        if (lines.Count > 0 && lines[0].Trim() == "---")
        {
            var close = Enumerable.Range(1, Math.Max(0, lines.Count - 1)).FirstOrDefault(at => lines[at].Trim() == "---", -1);
            if (close > 0) { Add(0, close, "frontmatter"); index = close + 1; }
        }

        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Trim().Length == 0) { index++; continue; }
            var start = index;
            var fence = Fence.Match(line);
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value;
                index++;
                while (index < lines.Count && !lines[index].TrimStart().StartsWith(marker, StringComparison.Ordinal)) index++;
                Add(start, Math.Min(index, lines.Count - 1), "code");
                index++;
            }
            else if (Heading.IsMatch(line))
            {
                section = line.Trim();
                Add(start, start, "heading");
                index++;
            }
            else if (Table.IsMatch(line))
            {
                while (index + 1 < lines.Count && Table.IsMatch(lines[index + 1])) index++;
                Add(start, index++, "table");
            }
            else if (Quote.IsMatch(line))
            {
                while (index + 1 < lines.Count && Quote.IsMatch(lines[index + 1])) index++;
                Add(start, index++, "quote");
            }
            else if (Rule.IsMatch(line))
            {
                Add(start, index++, "rule");
            }
            else if (Item.IsMatch(line))
            {
                while (index + 1 < lines.Count && Continues(lines[index + 1])) index++;
                Add(start, index++, "item");
            }
            else
            {
                while (index + 1 < lines.Count && Continues(lines[index + 1])) index++;
                Add(start, index++, "paragraph");
            }
        }

        return blocks;
    }

    private static bool Continues(string line)
        => line.Trim().Length > 0 && !Item.IsMatch(line) && !Heading.IsMatch(line) && !Fence.IsMatch(line) && !Table.IsMatch(line) && !Quote.IsMatch(line);

    private static string Dedent(List<string> lines)
    {
        var indent = lines.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart()));
    }

    // The heading a line is under, for the prompt: the nearest heading above it.
    public static string? SectionOf(IReadOnlyList<PlanBlock> blocks, int line)
        => blocks.LastOrDefault(block => block.Start <= line) is { } block ? block.Kind == "heading" ? block.Text.Trim() : block.Section : null;
}
