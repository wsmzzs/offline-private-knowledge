// 用途：读取严格 UTF-8 的 TXT/Markdown，保留标题层级、自然段和代码块。
using System.Text;
using System.Text.RegularExpressions;

namespace PrivateKnowledge.Api.Knowledge.Parsing;

/// <summary>原有纯文本能力的独立解析器。</summary>
internal static class TextDocumentParser
{
    /// <summary>把文字中的标题与正文分开；代码块里的 # 不作为章节。</summary>
    /// <param name="bytes">UTF-8 原始字节，可有 BOM。</param>
    /// <param name="result">接收出处和自然段的集合。</param>
    /// <param name="token">服务停止信号，每个自然段检查一次。</param>
    public static void Parse(byte[] bytes, ParsedDocument result, CancellationToken token)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        result.SetOriginalText(text);
        var titles = new string[6];
        var section = "正文";
        var fenced = false;
        var buffer = new StringBuilder();
        var tableNumber = 0;
        foreach (var block in Regex.Split(text, @"\n\s*\n"))
        {
            token.ThrowIfCancellationRequested();
            foreach (var line in block.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) fenced = !fenced;
                var heading = fenced ? Match.Empty : Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                if (heading.Success)
                {
                    Flush();
                    var depth = heading.Groups[1].Value.Length - 1;
                    titles[depth] = heading.Groups[2].Value.Trim();
                    for (var i = depth + 1; i < titles.Length; i++) titles[i] = "";
                    section = string.Join(" / ", titles.Where(t => !string.IsNullOrEmpty(t)));
                }
                else buffer.AppendLine(line);
            }
            // 代码块完整保留；普通正文在空行处形成自然段。
            if (!fenced) Flush();
            else buffer.AppendLine();
        }
        Flush();

        // 统一交给总量检查；这里不访问数据库。
        void Flush()
        {
            var content = buffer.ToString().Trim();
            buffer.Clear();
            var lines = content.Split('\n');
            // 常规 Markdown 表格按数据行生成片段，每行重复原表头，字段和值不会在切分时失去关联。
            // 原文查看仍返回 SetOriginalText 保存的原文件；这里仅调整用于检索的表示。
            if (lines.Length >= 3 && lines[0].TrimStart().StartsWith('|') &&
                Regex.IsMatch(lines[1], @"^\s*\|?\s*:?-{3,}:?\s*\|[\s|:\-]*$") &&
                lines.All(line => line.Contains('|')))
            {
                tableNumber++;
                for (var row = 2; row < lines.Length; row++)
                    result.Add($"{section} / 表格 {tableNumber} / 第 {row + 1} 行", lines[0] + "\n" + lines[1] + "\n" + lines[row]);
            }
            else result.Add(section, content);
        }
    }
}
