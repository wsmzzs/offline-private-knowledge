// 用途：对模型回答中的具体数量做保守检查，阻止把通道编号误说成温度上限或采样秒数。
using System.Text.RegularExpressions;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Questions;

/// <summary>只核对可机械检查的数量，不宣称能验证所有自然语言结论。</summary>
public static class EvidenceVerifier
{
    /// <summary>带计量单位的数字；中英文数字均保留原样，不进行未经业务确认的换算。</summary>
    private static readonly Regex Quantities = new(@"[零〇一二两三四五六七八九十百千万亿0-9]+(?:\.[0-9]+)?\s*(?:人民币|万元|小时|分钟|毫秒|摄氏度|张|个|元|天|年|月|日|箱|盒|次|秒|度|℃|%)", RegexOptions.Compiled);

    /// <summary>回答中的数量必须在本次所引用的原文中出现；缺少时拒答，避免用相关但无答案的引文支撑具体数值。</summary>
    /// <param name="answer">模型生成的回答。</param><param name="citations">模型实际选择、服务已核对编号的引文。</param>
    /// <returns>所有带单位的数量均能在原文找到时为 true；不能证明数量与业务实体之间的关系完全正确。</returns>
    public static bool QuantitiesSupported(string answer, IReadOnlyList<Citation> citations)
    {
        var excerpts = string.Join('\n', citations.Select(citation => citation.Excerpt));
        var known = Quantities.Matches(excerpts).Select(match => Regex.Replace(match.Value, @"\s+", "")).ToHashSet(StringComparer.Ordinal);
        return Quantities.Matches(answer).All(match => known.Contains(Regex.Replace(match.Value, @"\s+", "")));
    }
}
