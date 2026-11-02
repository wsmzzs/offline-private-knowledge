// 用途：集中处理官网 HTML 的变化；只取模型名称、简介和大小，不执行脚本或显示网页 HTML。
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Models;

/// <summary>官网没有公开的全量模型分页 API，因此解析其搜索页与版本页；页面改版时在这里适配。</summary>
public static class OllamaLibraryParser
{
    /// <summary>只接受官方 library 下的单段系列名称，防止请求变成任意网址。</summary>
    /// <param name="value">模型系列名，例如 qwen3。</param>
    /// <returns>是否为单段合法名称。</returns>
    public static bool IsFamily(string value) => Regex.IsMatch(value, @"\A[a-z0-9][a-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant);
    /// <summary>只接受系列:版本，不允许命令、路径、云端别名或换行。</summary>
    /// <param name="value">模型完整标签。</param>
    /// <returns>是否为本项目接受的本地标签语法。</returns>
    public static bool IsLocalTag(string value) => Regex.IsMatch(value,
        @"\A[a-z0-9][a-z0-9._-]{0,99}:[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)
        && !value.Contains("cloud", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析官网搜索页或滚动加载片段；过滤明显只有嵌入或分类用途的系列。</summary>
    /// <param name="html">由官方固定域名读取的 HTML。</param>
    /// <returns>系列列表和官网是否还有下一页。</returns>
    public static (IReadOnlyList<LibraryFamily> Items, bool HasMore) ParseSearch(string html)
    {
        var document = new HtmlDocument(); document.LoadHtml(html);
        var items = new List<LibraryFamily>();
        var cards = document.DocumentNode.SelectNodes("//a[starts-with(@href, '/library/')][.//h2]");
        foreach (var link in cards ?? Enumerable.Empty<HtmlNode>())
        {
            var name = link.GetAttributeValue("href", "")["/library/".Length..];
            if (!IsFamily(name)) continue;
            var badges = link.Descendants("span").Select(Text).Where(value => value is "tools" or "thinking" or "vision" or "embedding" or "decision" or "audio").Distinct().ToArray();
            if (badges.Contains("embedding") || badges.Contains("decision")) continue;
            items.Add(new(name, Text(link.SelectSingleNode(".//p")), string.Join(" · ", badges)));
        }
        // 无结果有官网明确的计数；如果连计数和链接都消失，视为页面改版，不能伪装成“没有模型”。
        if (cards is null && document.DocumentNode.SelectSingleNode("//*[@data-result-count]") is null &&
            !document.DocumentNode.InnerText.Contains("No models", StringComparison.OrdinalIgnoreCase))
            throw new ServiceException(502, "library_format_changed", "官方模型库页面格式已变化，暂时无法读取，请稍后重试或更新程序。");
        var more = document.DocumentNode.SelectSingleNode("//*[@hx-get and contains(@hx-get, 'page=')]") is not null;
        return (items.DistinctBy(item => item.Name).ToArray(), more);
    }

    /// <summary>读取具体版本，移动端与桌面端重复链接只保留一次；没有文件大小的云版本不列出。</summary>
    /// <param name="family">已经校验过的系列名称。</param>
    /// <param name="html">官网 /library/系列/tags 页面。</param>
    /// <returns>包含文件大小和保守配置估算的下载选项。</returns>
    public static IReadOnlyList<DownloadOption> ParseVersions(string family, string html)
    {
        var document = new HtmlDocument(); document.LoadHtml(html);
        if (document.DocumentNode.SelectSingleNode("//span[normalize-space(.)='embedding' or normalize-space(.)='decision']") is not null)
            throw new ServiceException(400, "unsupported_model_type", "该系列用于嵌入或分类，不能作为本项目的回答模型。");
        var items = new List<DownloadOption>();
        foreach (var link in document.DocumentNode.SelectNodes("//a[starts-with(@href, '/library/')]") ?? Enumerable.Empty<HtmlNode>())
        {
            var name = link.GetAttributeValue("href", "")["/library/".Length..];
            if (!name.StartsWith(family + ":", StringComparison.Ordinal) || !IsLocalTag(name)) continue;
            var size = Regex.Match(Text(link), @"(?<amount>\d+(?:\.\d+)?)\s*(?<unit>TB|GB|MB|KB)\b", RegexOptions.IgnoreCase);
            // 官网桌面链接只带名字，移动端链接有完整大小；只从有明确下载大小的卡片生成选项。
            if (!size.Success) continue;
            var amount = double.Parse(size.Groups["amount"].Value, CultureInfo.InvariantCulture);
            var gb = amount * (size.Groups["unit"].Value.ToUpperInvariant() switch { "TB" => 1000, "MB" => .001, "KB" => .000001, _ => 1 });
            var ram = Math.Max(8, Math.Ceiling((gb * 1.5 + 4) / 8) * 8);
            var disk = Math.Ceiling(gb * 1.2 + 1);
            items.Add(new(name, name, "官方本地版本；下载后还会核对是否支持本项目的聊天问答。", size.Value,
                $"估算建议：内存 ≥ {ram:0} GB，磁盘预留 ≥ {disk:0} GB。可用 CPU（较慢）；整模显卡加速还需容纳模型及上下文的显存。",
                "https://ollama.com/library/" + name));
        }
        var result = items.DistinctBy(item => item.Name).ToArray();
        if (result.Length == 0)
            throw new ServiceException(502, "library_versions_unavailable", "没有读取到可下载的本地版本，可能是云端专用模型或官网页面已变化。");
        return result;
    }

    /// <summary>网页文字转换为普通文本，保留原文含义但合并排版空格。</summary>
    /// <param name="node">简介、标签或版本链接节点。</param>
    /// <returns>可安全当作纯文字显示的内容。</returns>
    private static string Text(HtmlNode? node) => Regex.Replace(WebUtility.HtmlDecode(node?.InnerText ?? ""), @"\s+", " ").Trim();
}
