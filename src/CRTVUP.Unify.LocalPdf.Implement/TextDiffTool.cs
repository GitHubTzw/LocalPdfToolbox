using CRTVUP.Unify.LocalPdf.Abstractions;

namespace CRTVUP.Unify.LocalPdf.Implement;

/// <summary>行级文本对比（Myers 算法）与二进制对比；全部在本机内存中完成。</summary>
public sealed class TextDiffTool : ITextDiffTool
{
    public const int MaxLinesPerSide = 20000;
    /// <summary>编辑距离上限；超过时退化为“公共前后缀 + 中间整块差异”，避免占用过多内存。</summary>
    private const int MaxEditDistance = 1500;

    public DiffResult Compare(string left, string right, DiffOptions options)
    {
        var leftLines = SplitLines(left ?? "");
        var rightLines = SplitLines(right ?? "");
        if (options.IgnoreCommentLines)
        {
            leftLines = leftLines.Where(line => !IsCommentLine(line)).ToArray();
            rightLines = rightLines.Where(line => !IsCommentLine(line)).ToArray();
        }
        if (options.IgnoreBlankLines)
        {
            leftLines = leftLines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            rightLines = rightLines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        }
        if (leftLines.Length > MaxLinesPerSide || rightLines.Length > MaxLinesPerSide)
            throw new PdfToolException($"单侧最多对比 {MaxLinesPerSide} 行，请先拆分文件。");

        var leftKeys = leftLines.Select(line => Normalize(line, options)).ToArray();
        var rightKeys = rightLines.Select(line => Normalize(line, options)).ToArray();
        var rows = Align(Myers(leftKeys, rightKeys, out var truncated), leftLines, rightLines);

        var added = rows.Count(row => row.Kind == DiffLineKind.Added);
        var removed = rows.Count(row => row.Kind == DiffLineKind.Removed);
        var changed = rows.Count(row => row.Kind == DiffLineKind.Changed);
        var identical = added == 0 && removed == 0 && changed == 0;
        var summary = identical
            ? $"两侧内容一致，共 {rows.Count} 行。"
            : $"共 {rows.Count} 行：新增 {added} 行，删除 {removed} 行，修改 {changed} 行。";
        if (truncated) summary += " 差异过多，中间部分按整块差异显示。";
        return new DiffResult(rows, added, removed, changed, identical, summary);
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static string Normalize(string line, DiffOptions options)
    {
        var key = line;
        if (options.IgnoreTrailingWhitespace) key = key.TrimEnd();
        if (options.IgnoreWhitespace)
        {
            var builder = new System.Text.StringBuilder(key.Length);
            foreach (var ch in key)
                if (!char.IsWhiteSpace(ch)) builder.Append(ch);
            key = builder.ToString();
        }
        return options.IgnoreCase ? key.ToUpperInvariant() : key;
    }

    /// <summary>以 // 或 # 开头的行视为注释行；只在勾选“忽略注释行”时生效。</summary>
    private static bool IsCommentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('#');
    }

    /// <summary>行内差异允许的最大词数；超过时整行标为差异，避免界面卡顿。</summary>
    private const int MaxInlineTokens = 400;

    /// <summary>
    /// 返回 left 一侧的行内片段：left 独有的词标为 Highlight。
    /// 需要右侧时把两个参数对调再调用一次即可。
    /// </summary>
    public IReadOnlyList<InlineSegment> CompareInline(string left, string right)
    {
        var a = Tokenize(left ?? "");
        var b = Tokenize(right ?? "");
        if (a.Count == 0) return [];
        if (a.Count > MaxInlineTokens || b.Count > MaxInlineTokens) return [new InlineSegment(left ?? "", true)];

        var common = LongestCommonIndexes(a, b);
        var segments = new List<InlineSegment>();
        for (var index = 0; index < a.Count; index++)
        {
            var highlight = !common.Contains(index);
            if (segments.Count > 0 && segments[^1].Highlight == highlight)
                segments[^1] = segments[^1] with { Text = segments[^1].Text + a[index] };
            else
                segments.Add(new InlineSegment(a[index], highlight));
        }
        return segments;
    }

    /// <summary>按“字母数字串”和“单个符号”切词，让行内高亮落在词一级。</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in line)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
                continue;
            }
            if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            tokens.Add(ch.ToString());
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>经典 LCS 动态规划，返回左侧参与公共子序列的下标集合。</summary>
    private static HashSet<int> LongestCommonIndexes(List<string> left, List<string> right)
    {
        var n = left.Count;
        var m = right.Count;
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);

        var result = new HashSet<int>();
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal)) { result.Add(x); x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) x++;
            else y++;
        }
        return result;
    }

    private static List<Edit> Myers(string[] left, string[] right, out bool truncated)
    {
        truncated = false;
        var n = left.Length;
        var m = right.Length;
        if (n + m == 0) return [];
        var bound = Math.Min(n + m, MaxEditDistance);
        var offset = bound + 1;
        var v = new int[2 * bound + 3];
        var trace = new List<int[]>(bound + 1);
        for (var d = 0; d <= bound; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset])
                    ? v[k + 1 + offset]
                    : v[k - 1 + offset] + 1;
                var y = x - k;
                while (x < n && y < m && string.Equals(left[x], right[y], StringComparison.Ordinal)) { x++; y++; }
                v[k + offset] = x;
                if (x >= n && y >= m) return Backtrack(trace, offset, n, m);
            }
        }
        truncated = true;
        return Coarse(left, right);
    }

    private static List<Edit> Backtrack(List<int[]> trace, int offset, int n, int m)
    {
        var edits = new List<Edit>();
        var x = n;
        var y = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var v = trace[d];
            var k = x - y;
            var previousK = k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]) ? k + 1 : k - 1;
            var previousX = v[previousK + offset];
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY)
            {
                edits.Add(new Edit(x - 1, y - 1, DiffLineKind.Equal));
                x--;
                y--;
            }
            if (d > 0)
            {
                if (x == previousX) edits.Add(new Edit(-1, y - 1, DiffLineKind.Added));
                else edits.Add(new Edit(x - 1, -1, DiffLineKind.Removed));
            }
            x = previousX;
            y = previousY;
        }
        edits.Reverse();
        return edits;
    }

    private static List<Edit> Coarse(string[] left, string[] right)
    {
        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length
            && string.Equals(left[prefix], right[prefix], StringComparison.Ordinal)) prefix++;
        var suffix = 0;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix
            && string.Equals(left[left.Length - 1 - suffix], right[right.Length - 1 - suffix], StringComparison.Ordinal)) suffix++;
        var edits = new List<Edit>();
        for (var index = 0; index < prefix; index++) edits.Add(new Edit(index, index, DiffLineKind.Equal));
        for (var index = prefix; index < left.Length - suffix; index++) edits.Add(new Edit(index, -1, DiffLineKind.Removed));
        for (var index = prefix; index < right.Length - suffix; index++) edits.Add(new Edit(-1, index, DiffLineKind.Added));
        for (var index = 0; index < suffix; index++)
            edits.Add(new Edit(left.Length - suffix + index, right.Length - suffix + index, DiffLineKind.Equal));
        return edits;
    }

    private static List<DiffLine> Align(List<Edit> edits, string[] left, string[] right)
    {
        var rows = new List<DiffLine>();
        var index = 0;
        while (index < edits.Count)
        {
            if (edits[index].Kind == DiffLineKind.Equal)
            {
                var edit = edits[index++];
                rows.Add(new DiffLine(edit.Left + 1, left[edit.Left], edit.Right + 1, right[edit.Right], DiffLineKind.Equal));
                continue;
            }
            var removed = new List<int>();
            var added = new List<int>();
            while (index < edits.Count && edits[index].Kind != DiffLineKind.Equal)
            {
                if (edits[index].Kind == DiffLineKind.Removed) removed.Add(edits[index].Left);
                else added.Add(edits[index].Right);
                index++;
            }
            var paired = Math.Min(removed.Count, added.Count);
            for (var item = 0; item < paired; item++)
                rows.Add(new DiffLine(removed[item] + 1, left[removed[item]], added[item] + 1, right[added[item]], DiffLineKind.Changed));
            for (var item = paired; item < removed.Count; item++)
                rows.Add(new DiffLine(removed[item] + 1, left[removed[item]], null, null, DiffLineKind.Removed));
            for (var item = paired; item < added.Count; item++)
                rows.Add(new DiffLine(null, null, added[item] + 1, right[added[item]], DiffLineKind.Added));
        }
        return rows;
    }

    private readonly record struct Edit(int Left, int Right, DiffLineKind Kind);
}

/// <summary>按字节比较两个本地文件；不解析内容、不写日志。</summary>
public sealed class BinaryDiffTool : IBinaryDiffTool
{
    public BinaryDiffResult Compare(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right)
    {
        var a = left.Span;
        var b = right.Span;
        var shorter = Math.Min(a.Length, b.Length);
        long? first = null;
        for (var index = 0; index < shorter; index++)
            if (a[index] != b[index]) { first = index; break; }
        first ??= a.Length == b.Length ? null : shorter;
        var identical = first is null;
        var summary = identical
            ? $"两个文件完全一致，共 {a.Length} 字节。"
            : $"第 {first} 个字节起不同（从 0 开始计数）；左侧 {a.Length} 字节，右侧 {b.Length} 字节。";
        return new BinaryDiffResult(identical, a.Length, b.Length, first, summary);
    }
}
