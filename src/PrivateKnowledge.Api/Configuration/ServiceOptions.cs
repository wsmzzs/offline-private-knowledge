// 用途：集中定义可修改的部署配置，并在启动时发现不合理的路径和参数。
namespace PrivateKnowledge.Api.Configuration;

/// <summary>知识资料处理与检索配置；从 appsettings.json 的 Knowledge 节读取。</summary>
public sealed class KnowledgeOptions
{
    /// <summary>原始资料根目录；每个一级子目录是一套知识库。</summary>
    public string RootPath { get; set; } = "SampleKnowledge";
    /// <summary>SQLite 的保存目录，必须与原始资料目录分开。</summary>
    public string DataPath { get; set; } = "data";
    /// <summary>两次完整检查之间的秒数；用于补偿文件通知丢失。</summary>
    public int ScanSeconds { get; set; } = 5;
    /// <summary>单文件最大字节数；超限时标记知识库失败，避免悄悄漏读资料。</summary>
    public int MaxFileBytes { get; set; } = 20 * 1024 * 1024;
    /// <summary>单知识库原始文件总字节上限，默认 100 MiB，避免一次把大量大文件读入内存。</summary>
    public int MaxTotalFileBytes { get; set; } = 100 * 1024 * 1024;
    /// <summary>DOCX/XLSX 压缩包展开后的总字节上限，默认 64 MiB；不在磁盘解压。</summary>
    public int MaxExpandedBytes { get; set; } = 64 * 1024 * 1024;
    /// <summary>单文件提取文字与出处的总字符上限，防止大资料耗尽内存。</summary>
    public int MaxExtractedCharacters { get; set; } = 1_000_000;
    /// <summary>文字型 PDF 页数上限，默认 300；超过时按章节拆分。</summary>
    public int MaxPdfPages { get; set; } = 300;
    /// <summary>单知识库文件上限，限制本机原型的资源使用。</summary>
    public int MaxFilesPerBase { get; set; } = 200;
    /// <summary>单知识库段落上限；超过上限需整理资料或扩大配置。</summary>
    public int MaxChunksPerBase { get; set; } = 2000;
    /// <summary>单段最大字符数；长章节会按段落继续拆分。</summary>
    public int ChunkCharacters { get; set; } = 1400;
    /// <summary>交给模型的最多段落数，避免把整本手册塞进请求。</summary>
    public int TopChunks { get; set; } = 4;
    /// <summary>问题关键词至少命中多少比例才认为有候选证据，不代表答案正确率。</summary>
    public double MinimumKeywordCoverage { get; set; } = 0.2;
    /// <summary>是否生成并使用语义向量；默认关闭，现有聊天模型即可开始。</summary>
    public bool UseEmbeddings { get; set; }
    /// <summary>语义相似度候选门槛；需用实际中文问题校准，不是置信度。</summary>
    public double MinimumSemanticScore { get; set; } = 0.6;
    /// <summary>两路检索各自保留的候选数；先扩大查找范围，再合并排名。</summary>
    public int RetrievalCandidates { get; set; } = 12;
    /// <summary>后台一次计算几个片段；小批次给前台提问让出执行机会。</summary>
    public int EmbeddingBatchSize { get; set; } = 4;
    /// <summary>交给回答模型的资料 JSON 最大 UTF-8 字节数；放不下就跳过完整片段。</summary>
    public int EvidenceMaxBytes { get; set; } = 4300;

    /// <summary>将相对路径解析到程序目录并检查配置；配置错误时直接阻止启动。</summary>
    /// <param name="programDirectory">程序所在目录，例如发布包中的服务文件夹。</param>
    /// <returns>无返回值；检查通过后 RootPath 和 DataPath 都是绝对路径。</returns>
    public void Validate(string programDirectory)
    {
        RootPath = Path.GetFullPath(RootPath, programDirectory);
        DataPath = Path.GetFullPath(DataPath, programDirectory);
        // 分开保存索引，否则扫描器可能把生成的数据再次当成知识资料。
        if (ContainsPath(RootPath, DataPath) || ContainsPath(DataPath, RootPath))
            throw new InvalidOperationException("Knowledge 的资料目录和数据目录不能相同或互相包含。");
        if (ScanSeconds < 1 || MaxFileBytes < 1 || MaxTotalFileBytes < 1 || MaxExpandedBytes < 1 || MaxExtractedCharacters < 1 ||
            MaxPdfPages < 1 || MaxFilesPerBase < 1 || MaxChunksPerBase < 1 ||
            ChunkCharacters < 200 || ChunkCharacters > 4000 || TopChunks < 1 || TopChunks > 6 ||
            MinimumKeywordCoverage is <= 0 or > 1 || MinimumSemanticScore is <= 0 or > 1 ||
            RetrievalCandidates is < 4 or > 100 || EmbeddingBatchSize is < 1 or > 16 || EvidenceMaxBytes is < 1000 or > 4300)
            throw new InvalidOperationException("Knowledge 参数超出范围，请查看 README 的配置说明。");
    }

    /// <summary>按完整目录边界判断包含关系，避免 D:\data 与 D:\database 被误认为同一目录。</summary>
    /// <param name="parent">候选父目录的绝对路径。</param>
    /// <param name="child">待检查目录的绝对路径。</param>
    /// <returns>两者相同或 child 位于 parent 中时为 true。</returns>
    private static bool ContainsPath(string parent, string child) =>
        string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), child.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>本地模型接口配置；该服务不会自动下载模型或转发到公网模型。</summary>
public sealed class ModelOptions
{
    /// <summary>Ollama 地址；第一版要求为本机 HTTP 地址。</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/";
    /// <summary>已安装的聊天模型名称，例如 qwen3:1.7b。</summary>
    public string ChatModel { get; set; } = "qwen3:1.7b";
    /// <summary>可选的嵌入模型名称；启用语义检索时必须填写。</summary>
    public string EmbeddingModel { get; set; } = "";
    /// <summary>等待模型和排队的最大秒数；超时后请求返回明确错误。</summary>
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>为 true 时嵌入请求使用 CPU；不修改 Ollama 全局设置。</summary>
    public bool EmbeddingOnCpu { get; set; } = true;
    /// <summary>一批向量完成后的驻留秒数；0 表示释放，300 表示保留五分钟。</summary>
    public int EmbeddingKeepAliveSeconds { get; set; } = 0;
    /// <summary>回答模型完成后的驻留秒数；默认五分钟，内存紧张时可改为 0。</summary>
    public int ChatKeepAliveSeconds { get; set; } = 300;
}
