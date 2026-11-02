// 用途：按页提取文字型 PDF 的正文；扫描图片页明确提示需要 OCR，不把空结果当成成功。
using System.Text.RegularExpressions;
using PrivateKnowledge.Api.Configuration;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PrivateKnowledge.Api.Knowledge.Parsing;

/// <summary>PDF 文字解析器；保留物理页码，不承诺复杂多栏或表格的版式还原。</summary>
internal static class PdfDocumentParser
{
    /// <summary>读取每一页的可提取文字，移除多页完全重复的边缘行，再形成自然段。</summary>
    /// <param name="bytes">PDF 原始字节。</param>
    /// <param name="result">接收按页标注的正文。</param>
    /// <param name="options">最大页数和提取文字总量。</param>
    /// <param name="token">服务停止信号，在每页检查。</param>
    public static void Parse(byte[] bytes, ParsedDocument result, KnowledgeOptions options, CancellationToken token)
    {
        using var document = PdfDocument.Open(bytes);
        if (document.NumberOfPages > options.MaxPdfPages)
            throw new InvalidDataException("PDF 页数超过 MaxPdfPages 限制，请按章节拆分。");
        var pages = new List<(int Number, string Text)>();
        long total = 0;
        foreach (var page in document.GetPages())
        {
            token.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page, true).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                // 空白装订页可以跳过；没有文字却包含图片的页可能有业务内容，必须阻止漏读。
                if (page.NumberOfImages > 0)
                    throw new InvalidDataException($"PDF 第 {page.Number} 页只有图片、没有可提取文字，需要 OCR；本版不支持扫描页。");
                continue;
            }
            total += text.Length;
            if (total > options.MaxExtractedCharacters)
                throw new InvalidDataException("PDF 提取文字超过 MaxExtractedCharacters 限制，请拆分文件。");
            pages.Add((page.Number, text));
        }

        // 只有至少三页且多数页面的边缘完全相同时才移除；不猜测正文或按模型改写。
        var headers = RepeatedEdges(pages, true);
        var footers = RepeatedEdges(pages, false);
        foreach (var page in pages)
        {
            token.ThrowIfCancellationRequested();
            var lines = page.Text.Split('\n').ToList();
            if (lines.Count > 2 && headers.Contains(lines[0].Trim())) lines.RemoveAt(0);
            if (lines.Count > 2 && footers.Contains(lines[^1].Trim())) lines.RemoveAt(lines.Count - 1);
            var text = string.Join('\n', lines);
            foreach (var paragraph in Regex.Split(text, @"\n\s*\n"))
                result.Add($"第 {page.Number} 页", paragraph);
        }
    }

    /// <summary>找出至少三页且覆盖三分之二页面的相同首行或末行。</summary>
    /// <param name="pages">已经提取的非空页面。</param>
    /// <param name="first">true 检查页眉，false 检查页脚。</param>
    /// <returns>可移除的完全相同边缘行，不对不同数字的页码做猜测。</returns>
    private static HashSet<string> RepeatedEdges(List<(int Number, string Text)> pages, bool first)
    {
        if (pages.Count < 3) return [];
        return pages.Select(page => page.Text.Split('\n')).Where(lines => lines.Length > 2)
            .Select(lines => (first ? lines[0] : lines[^1]).Trim()).Where(line => line.Length > 0)
            .GroupBy(line => line, StringComparer.Ordinal).Where(group => group.Count() >= Math.Max(3, (int)Math.Ceiling(pages.Count * 2.0 / 3)))
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
    }
}
