// 用途：按格式选择本地解析器，再统一拆分片段；不修改源文件，也不调用模型整理资料。
using System.Security.Cryptography;
using System.Text;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Knowledge.Parsing;

namespace PrivateKnowledge.Api.Knowledge;

/// <summary>资料读取入口；不同格式只负责提供有出处的文字，共用后续存储和检索。</summary>
public static class DocumentReader
{
    /// <summary>处理规则版本；升级时让旧快照重新解析，防止继续复用旧格式的片段。</summary>
    public const string PipelineVersion = "reader-v3-structured-table-sentences";

    /// <summary>判断文件是否属于本版可以解析的格式；忽略 Office 保存时生成的临时锁文件。</summary>
    /// <param name="path">扫描器发现的路径，如 项目甲/流程.docx。</param>
    /// <returns>正式的 MD、TXT、DOCX、PDF、XLSX 文件返回 true。</returns>
    public static bool Supports(string path) => !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal) &&
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".txt" or ".docx" or ".pdf" or ".xlsx";

    /// <summary>计算原始文件内容的指纹；Office 文件仍按原始字节检查变化。</summary>
    /// <param name="bytes">扫描器稳定读取的全部文件字节。</param>
    /// <returns>大写 SHA256 字符串。</returns>
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>提取文字并生成片段；格式错误、扫描件和超限文件会明确失败。</summary>
    /// <param name="baseId">一级目录名，例如 demo-a。</param>
    /// <param name="relativePath">项目内相对路径，例如 指南/操作流程.pdf。</param>
    /// <param name="bytes">原始文件字节；不是提前转好的 UTF-8 文字。</param>
    /// <param name="maxCharacters">单片段最大字符数，例如 1400。</param>
    /// <param name="options">文件展开、文字总量和 PDF 页数限制；测试可省略。</param>
    /// <param name="token">服务关闭时发出的停止信号，在页、段落和行边界检查。</param>
    /// <returns>包含提取文字和出处片段的快照；尚未生成语义向量。</returns>
    public static KnowledgeDocument Read(string baseId, string relativePath, byte[] bytes, int maxCharacters,
        KnowledgeOptions? options = null, CancellationToken token = default)
    {
        options ??= new KnowledgeOptions();
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        if (!Supports(relativePath)) throw new InvalidDataException("不支持此资料格式；请使用 MD、TXT、DOCX、文字型 PDF 或 XLSX。");
        token.ThrowIfCancellationRequested();
        if (bytes.Length > options.MaxFileBytes) throw new InvalidDataException("资料文件超过大小限制，请拆分文件。");
        var parsed = new ParsedDocument(options.MaxExtractedCharacters);
        try
        {
            // 每个解析器只读取当前内存中的文件；不会启动 Office 或访问资料内的外部链接。
            switch (extension)
            {
                case ".md": case ".txt": TextDocumentParser.Parse(bytes, parsed, token); break;
                case ".docx": WordDocumentParser.Parse(bytes, parsed, options, token); break;
                case ".xlsx": SpreadsheetDocumentParser.Parse(bytes, parsed, options, token); break;
                case ".pdf": PdfDocumentParser.Parse(bytes, parsed, options, token); break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (InvalidDataException) { throw; }
        catch (DecoderFallbackException) { throw new InvalidDataException("TXT/MD 必须保存为 UTF-8 编码，请转换编码后再试。"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 第三方解析器的异常可能带有文件内部细节；界面只显示可操作的格式说明。
            throw new InvalidDataException($"{extension} 解析失败：文件可能损坏、加密或包含不支持的结构；请重新另存为普通文件。", ex);
        }

        var hash = Hash(bytes);
        var id = Hash(Encoding.UTF8.GetBytes(baseId + "\n" + relativePath + "\n" + hash));
        var chunks = DocumentChunker.Split(parsed.Parts, id, Path.GetFileName(relativePath), maxCharacters);
        if (chunks.Count == 0) throw new InvalidDataException("文件没有可检索文字；图片内容需要 OCR，本版尚未支持。");
        // 原文查看接口返回提取后的阅读文本；保留原文件哈希，替换文件会使旧引用失效。
        return new KnowledgeDocument(id, relativePath, hash, parsed.ToText(), chunks);
    }
}
