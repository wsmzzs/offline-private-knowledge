// 用途：让 Word、PDF、Excel 和纯文本使用同一份“出处＋文字”结构，限制解压和提取后的体积。
using System.IO.Compression;
using System.Text;
using PrivateKnowledge.Api.Configuration;

namespace PrivateKnowledge.Api.Knowledge.Parsing;

/// <summary>一个自然段或表格行；Section 可以包含章节、页码、工作表和行号。</summary>
/// <param name="Section">例如“工作表：流程 / 第 3 行”。</param>
/// <param name="Text">真实提取的内容；表格行会重复列名，避免值与表头分离。</param>
internal sealed record DocumentPart(string Section, string Text);

/// <summary>收集解析结果，集中检查文字总量；避免小压缩文件展开成无限大的索引。</summary>
/// <param name="maxCharacters">单文件提取文字和出处的字符总上限。</param>
internal sealed class ParsedDocument(int maxCharacters)
{
    /// <summary>各解析器按阅读顺序追加的内容。</summary>
    public List<DocumentPart> Parts { get; } = [];
    /// <summary>已收集的字符数，包括出处。</summary>
    private int _characters;
    /// <summary>纯文本保留规范化后的完整原文；Office/PDF 则由提取结果生成阅读文字。</summary>
    private string? _originalText;

    /// <summary>为 TXT/MD 保留完整原文，不改变原文查看接口原有体验。</summary>
    /// <param name="text">已去除 BOM 并统一换行的 UTF-8 文字。</param>
    public void SetOriginalText(string text)
    {
        if (text.Length > maxCharacters) throw new InvalidDataException("文件文字超过 MaxExtractedCharacters 限制，请拆分资料。");
        _originalText = text;
    }

    /// <summary>添加有出处的自然段；忽略空白段，超限立即停止整个文件处理。</summary>
    /// <param name="section">原始位置，如“第 2 页”。</param>
    /// <param name="text">该位置提取的正文。</param>
    public void Add(string section, string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var length = (long)_characters + text.Length + section.Length;
        if (length > maxCharacters) throw new InvalidDataException("文件提取后的文字超过 MaxExtractedCharacters 限制，请拆分资料。");
        _characters = (int)length;
        Parts.Add(new DocumentPart(section, text));
    }

    /// <summary>生成供原文面板阅读的文字；切换出处时插入位置标记。</summary>
    /// <returns>提取文字，不是 PDF/Word 的版式预览，也不是模型生成的摘要。</returns>
    public string ToText()
    {
        if (_originalText is not null) return _originalText;
        var result = new StringBuilder();
        string? previous = null;
        foreach (var part in Parts)
        {
            if (part.Section != previous) result.AppendLine("【" + part.Section + "】");
            result.AppendLine(part.Text).AppendLine();
            previous = part.Section;
        }
        return result.ToString().Trim();
    }

    /// <summary>先检查 DOCX/XLSX 压缩包的展开体积；不把压缩包解压到磁盘。</summary>
    /// <param name="bytes">扫描器读取的 Office 文件。</param>
    /// <param name="options">包含 MaxExpandedBytes 的部署配置。</param>
    /// <param name="token">关闭服务时停止检查。</param>
    public static void CheckOfficePackage(byte[] bytes, KnowledgeOptions options, CancellationToken token)
    {
        using var stream = new MemoryStream(bytes, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            total += entry.Length;
            if (total > options.MaxExpandedBytes)
                throw new InvalidDataException("Office 文件展开后超过 MaxExpandedBytes 限制，请减少图片或拆分文件。");
        }
    }
}
