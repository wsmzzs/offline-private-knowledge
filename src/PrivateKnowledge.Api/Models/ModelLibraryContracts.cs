// 用途：定义官方模型系列、具体版本和分页结果；与问答和私有知识资料完全分开。
namespace PrivateKnowledge.Api.Models;

/// <summary>官网中的一个模型系列，例如 qwen3；还未选定具体大小和量化版本。</summary>
/// <param name="Name">官方系列名称。</param>
/// <param name="Description">官网提供的原始简介，不通过模型改写。</param>
/// <param name="Capabilities">官网标记，例如 tools、thinking、vision。</param>
public sealed record LibraryFamily(string Name, string Description, string Capabilities);

/// <summary>一次最多五个系列；NextOffset 为空表示当前搜索已到末尾。</summary>
/// <param name="Items">本批系列。</param>
/// <param name="NextOffset">下一批偏移量。</param>
/// <param name="FromCache">是否复用了近期查询。</param>
/// <param name="IsStale">是否因官网不可用而返回过期缓存。</param>
public sealed record LibraryPage(IReadOnlyList<LibraryFamily> Items, int? NextOffset, bool FromCache, bool IsStale);

/// <summary>同一系列的本地版本列表；版本大小来自官网。</summary>
/// <param name="Items">下载选项，已经排除云端版本。</param>
/// <param name="FromCache">是否复用缓存。</param>
/// <param name="IsStale">官网失败时是否使用旧缓存。</param>
public sealed record LibraryVersions(IReadOnlyList<DownloadOption> Items, bool FromCache, bool IsStale);

/// <summary>可下载模型来源；生产使用官网，测试使用明确的模拟页面或列表。</summary>
public interface IModelLibrary
{
    /// <summary>按关键字读取五个系列；不查询全部版本，也不下载模型。</summary>
    /// <param name="query">搜索框内容，例如 qwen；空白表示浏览官网列表。</param>
    /// <param name="offset">从第几项继续，初始为零。</param>
    /// <param name="token">关窗或断开连接时取消。</param>
    /// <returns>本批系列及下一批偏移。</returns>
    Task<LibraryPage> SearchAsync(string query, int offset, CancellationToken token);
    /// <summary>选中系列后才读取其具体版本。</summary>
    /// <param name="family">例如 qwen3，不接受网址或路径。</param>
    /// <param name="token">请求取消信号。</param>
    /// <returns>官方存在的本地版本及配置估算。</returns>
    Task<LibraryVersions> GetVersionsAsync(string family, CancellationToken token);
}
