// 用途：统一把有出处的自然段合并成检索片段，不混合不同页、工作表行或章节。
using System.Text;
using System.Text.RegularExpressions;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Knowledge.Parsing;

/// <summary>与文件格式无关的片段生成器。</summary>
internal static class DocumentChunker
{
    /// <summary>按自然段边界组装片段；长正文先按换行和句子拆分，表格行保持完整。</summary>
    /// <param name="parts">按文档顺序提取的文字。</param>
    /// <param name="documentId">原始文件内容决定的文档编号。</param>
    /// <param name="title">文件名，如 操作流程.docx。</param>
    /// <param name="maxCharacters">最大片段长度，如 1400。</param>
    /// <returns>可供现有检索器读取的片段集合。</returns>
    public static List<KnowledgeChunk> Split(IEnumerable<DocumentPart> parts, string documentId, string title, int maxCharacters)
    {
        var chunks = new List<KnowledgeChunk>();
        var buffer = new StringBuilder();
        var section = "正文";
        foreach (var part in parts)
        {
            // 表格字段值和代码块不能按句号任意切开；超长时明确要求整理，避免生成误导片段。
            var atomic = part.Section.Contains("表格", StringComparison.Ordinal) || part.Section.StartsWith("工作表：", StringComparison.Ordinal) ||
                part.Text.TrimStart().StartsWith("```", StringComparison.Ordinal);
            if (atomic && part.Text.Length > maxCharacters)
                throw new InvalidDataException($"位置“{part.Section}”的表格行或代码块超过 {maxCharacters} 字符，请按完整记录拆分资料。");
            var pieces = part.Text.Length <= maxCharacters ? [part.Text] : SplitLongText(part.Text, maxCharacters);
            foreach (var piece in pieces)
            {
                // 不跨 PDF 页、Excel 行或章节合并；也不会为了塞进长度而截断下一行。
                if (part.Section != section || buffer.Length + piece.Length + 2 > maxCharacters) Flush();
                section = part.Section;
                if (buffer.Length > 0) buffer.AppendLine().AppendLine();
                buffer.Append(piece);
                if (buffer.Length > maxCharacters / 2) Flush();
            }
        }
        Flush();
        return chunks;

        // 只保存真实正文；标题本身不生成空片段。
        void Flush()
        {
            if (buffer.Length == 0) return;
            chunks.Add(new KnowledgeChunk($"{documentId}:{chunks.Count}", documentId, title, section, buffer.ToString(), null));
            buffer.Clear();
        }
    }

    /// <summary>拆分超长正文；优先句末和换行，极长无标点文本最后才按字符边界分开。</summary>
    /// <param name="text">同一出处中的完整正文。</param>
    /// <param name="limit">单片段字符上限。</param>
    /// <returns>保持原顺序、没有改写内容的片段。</returns>
    private static string[] SplitLongText(string text, int limit)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in Regex.Split(text, @"(?<=[。！？；\n])"))
        {
            if (current.Length + sentence.Length > limit && current.Length > 0)
            { pieces.Add(current.ToString()); current.Clear(); }
            var offset = 0;
            while (sentence.Length - offset > limit)
            {
                var length = limit;
                // 不在 UTF-16 代理对之间断开，例如正文中的表情字符。
                if (char.IsHighSurrogate(sentence[offset + length - 1])) length--;
                pieces.Add(sentence.Substring(offset, length));
                offset += length;
            }
            current.Append(sentence.AsSpan(offset));
        }
        if (current.Length > 0) pieces.Add(current.ToString());
        return pieces.ToArray();
    }
}
