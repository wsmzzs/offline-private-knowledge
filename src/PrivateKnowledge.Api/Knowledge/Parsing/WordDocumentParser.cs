// 用途：离线读取 DOCX 正文、标题和简单表格，不依赖本机 Word，不执行宏或外部链接。
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PrivateKnowledge.Api.Configuration;

namespace PrivateKnowledge.Api.Knowledge.Parsing;

/// <summary>Word 解析器；复杂合并表格明确拒绝，图片和页面版式不作为文字证据。</summary>
internal static class WordDocumentParser
{
    /// <summary>按正文顺序提取自然段和表格；Word 页码取决于排版，因此引用使用标题和表格行。</summary>
    /// <param name="bytes">DOCX 文件字节。</param>
    /// <param name="result">统一的解析结果。</param>
    /// <param name="options">Office 展开体积限制。</param>
    /// <param name="token">服务停止信号，遍历正文时检查。</param>
    public static void Parse(byte[] bytes, ParsedDocument result, KnowledgeOptions options, CancellationToken token)
    {
        ParsedDocument.CheckOfficePackage(bytes, options, token);
        using var stream = new MemoryStream(bytes, false);
        using var document = WordprocessingDocument.Open(stream, false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("DOCX 缺少正文。");
        var body = main.Document?.Body ?? throw new InvalidDataException("DOCX 缺少正文。");
        if (body.Descendants<AltChunk>().Any()) throw new InvalidDataException("DOCX 包含嵌入式外部正文，请在 Word 中转换为普通段落后另存。");
        var styles = main.StyleDefinitionsPart?.Styles?.Elements<Style>()
            .Where(s => s.StyleId?.Value is not null).ToDictionary(s => s.StyleId!.Value!, StringComparer.Ordinal) ?? [];
        var headings = new string[9];
        var tableNumber = 0;
        Walk(body);

        // 内容控件可能包住正文或表格，递归处理容器；段落和表格只在各自分支处理一次。
        void Walk(OpenXmlElement parent)
        {
            foreach (var child in parent.ChildElements)
            {
                token.ThrowIfCancellationRequested();
                if (child is Paragraph paragraph)
                {
                    var text = ReadParagraph(paragraph);
                    var depth = HeadingDepth(paragraph, styles);
                    if (depth is >= 0 and < 9)
                    {
                        headings[depth] = text;
                        for (var i = depth + 1; i < headings.Length; i++) headings[i] = "";
                    }
                    else result.Add(CurrentSection(), text);
                }
                else if (child is Table table) ReadTable(table, ++tableNumber, CurrentSection(), result);
                else Walk(child);
            }
        }

        // 没有标题的文档仍可读取，位置显示“正文”。
        string CurrentSection() => headings.Any(h => !string.IsNullOrEmpty(h))
            ? string.Join(" / ", headings.Where(h => !string.IsNullOrEmpty(h))) : "正文";
    }

    /// <summary>提取段落可见文字，保留手动换行和制表符；不把域指令当成业务正文。</summary>
    /// <param name="paragraph">正文或单元格内的段落。</param>
    /// <returns>Word 保存的文字结果；自动编号的排版符号不会重新计算。</returns>
    private static string ReadParagraph(Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            // 已标记删除的文字不属于当前有效版本；修订中的新增正文仍保留。
            if (element.Ancestors<DeletedRun>().Any()) continue;
            if (element is Text item) text.Append(item.Text);
            else if (element is Break or CarriageReturn) text.AppendLine();
            else if (element is TabChar) text.Append('\t');
        }
        return text.ToString().Trim();
    }

    /// <summary>识别段落的大纲级别、标题样式及其继承关系。</summary>
    /// <param name="paragraph">当前段落。</param>
    /// <param name="styles">文档中的样式字典。</param>
    /// <returns>0 表示一级标题，1 表示二级标题；-1 表示普通正文。</returns>
    private static int HeadingDepth(Paragraph paragraph, Dictionary<string, Style> styles)
    {
        var direct = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        if (direct is not null) return direct.Value < 9 ? direct.Value : -1;
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (styleId is not null && visited.Add(styleId))
        {
            if (styles.TryGetValue(styleId, out var style))
            {
                var level = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
                if (level is not null) return level.Value < 9 ? level.Value : -1;
                var name = style.StyleName?.Val?.Value ?? styleId;
                var match = Regex.Match(name, @"^(?:heading|标题)\s*([1-9])$", RegexOptions.IgnoreCase);
                if (match.Success) return int.Parse(match.Groups[1].Value) - 1;
                styleId = style.BasedOn?.Val?.Value;
            }
            else
            {
                var match = Regex.Match(styleId, @"^Heading([1-9])$", RegexOptions.IgnoreCase);
                return match.Success ? int.Parse(match.Groups[1].Value) - 1 : -1;
            }
        }
        return -1;
    }

    /// <summary>按第一行表头组织简单表格；每行重复列名，避免检索后只剩孤立的值。</summary>
    /// <param name="table">原始 Word 表格。</param>
    /// <param name="number">文档内从 1 开始的表格序号。</param>
    /// <param name="section">表格所在章节。</param>
    /// <param name="result">有出处的文字集合。</param>
    private static void ReadTable(Table table, int number, string section, ParsedDocument result)
    {
        if (table.Descendants<Table>().Any() || table.Descendants<VerticalMerge>().Any() || table.Descendants<HorizontalMerge>().Any() ||
            table.Descendants<GridSpan>().Any(s => (s.Val?.Value ?? 1) != 1) ||
            table.Descendants<GridBefore>().Any() || table.Descendants<GridAfter>().Any())
            throw new InvalidDataException($"DOCX 第 {number} 个表格包含合并、嵌套或错位单元格，请整理成普通单行表头表格。");
        var rows = table.Elements<TableRow>().Select(row => row.Elements<TableCell>()
            .Select(cell => string.Join("\n", cell.Descendants<Paragraph>().Select(ReadParagraph))).ToArray()).ToArray();
        if (rows.Length == 0) return;
        var headers = rows[0];
        var named = headers.Where(header => !string.IsNullOrWhiteSpace(header)).ToArray();
        if (named.Distinct(StringComparer.Ordinal).Count() != named.Length)
            throw new InvalidDataException($"DOCX 第 {number} 个表格的表头重复，请给每一列使用不同名称。");
        result.Add($"{section} / 表格 {number} / 第 1 行（表头）", string.Join("；", headers));
        for (var row = 1; row < rows.Length; row++)
        {
            if (rows[row].Length != headers.Length) throw new InvalidDataException($"DOCX 第 {number} 个表格的列数不一致，请整理后重试。");
            var values = rows[row].Select((value, column) =>
                $"{(string.IsNullOrWhiteSpace(headers[column]) ? "第" + (column + 1) + "列" : headers[column])}：{value}");
            result.Add($"{section} / 表格 {number} / 第 {row + 1} 行", string.Join("；", values));
        }
    }
}
