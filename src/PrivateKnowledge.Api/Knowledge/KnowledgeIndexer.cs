// 用途：后台自动发现资料变化，稳定读取后发布快照；文件夹才是知识来源。
using System.Text;
using Microsoft.Extensions.Hosting;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Models;
using PrivateKnowledge.Api.Storage;

namespace PrivateKnowledge.Api.Knowledge;

/// <summary>资料扫描后台任务；通知只负责使旧状态失效，周期扫描负责最终核对。</summary>
public sealed class KnowledgeIndexer : BackgroundService
{
    /// <summary>资料、大小限制与扫描配置。</summary>
    private readonly KnowledgeOptions _options;
    /// <summary>快照存储。</summary>
    private readonly KnowledgeStore _store;
    /// <summary>只在启用语义检索时用于生成向量。</summary>
    private readonly IModelClient _model;
    /// <summary>仅记录处理状态，不记录问答正文。</summary>
    private readonly ILogger<KnowledgeIndexer> _logger;
    /// <summary>上一次看到的候选指纹；连续两次一致后才处理文件。</summary>
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    /// <summary>阻止测试扫描和后台扫描同时写入。</summary>
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    /// <summary>文件系统通知器；根目录恢复后可以重新创建。</summary>
    private FileSystemWatcher? _watcher;
    /// <summary>每次资料发生变化递增，用于阻止生成中的旧答案返回。</summary>
    private long _generation;
    /// <summary>最近一次扫描确认过的变化代号；初始为 -1，首次扫描前拒绝问答。</summary>
    private long _validatedGeneration = -1;

    /// <summary>注入资料处理所需组件，并把上次运行的状态先改为待核对。</summary>
    /// <param name="options">资料配置。</param>
    /// <param name="store">已初始化数据库。</param>
    /// <param name="model">本地模型适配器。</param>
    /// <param name="logger">服务日志。</param>
    public KnowledgeIndexer(KnowledgeOptions options, KnowledgeStore store, IModelClient model, ILogger<KnowledgeIndexer> logger)
    {
        _options = options; _store = store; _model = model; _logger = logger;
        foreach (var state in store.List()) store.SetState(state.Id, "checking", "服务刚启动，正在核对原始资料。");
    }

    /// <summary>当前资料变动代号；问答前后不一致时拒绝返回旧依据。</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>要求整个资料目录已核对；文件通知与扫描之间不会直接使用旧索引。</summary>
    /// <exception cref="ServiceException">目录离线或有尚未检查的文件变化时返回 503。</exception>
    public void EnsureCatalogChecked()
    {
        if (!Directory.Exists(_options.RootPath))
            throw new ServiceException(503, "knowledge_unavailable", "知识资料目录不可访问。");
        if (Generation != Interlocked.Read(ref _validatedGeneration))
            throw new ServiceException(503, "knowledge_updating", "正在检查资料变化，请稍后重试。");
    }

    /// <summary>持续扫描；服务关闭时由 stoppingToken 结束等待。</summary>
    /// <param name="stoppingToken">ASP.NET Core 在服务停止时提供的取消信号。</param>
    /// <returns>后台任务结束后完成。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ScanOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_options.ScanSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* 正常停止，不记录为故障。 */ }
    }

    /// <summary>扫描一轮；根目录不可读时保留旧快照但禁止问答，不执行批量删除。</summary>
    /// <param name="cancellationToken">服务停止或测试取消信号。</param>
    /// <returns>本轮所有知识库处理完成后结束。</returns>
    public async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            RejectLink(_options.RootPath);
            EnsureWatcher();
            var generation = Generation;
            // 先完整枚举成功，才能把未出现的旧目录标记为删除。
            var directories = Directory.GetDirectories(_options.RootPath).Order(StringComparer.Ordinal).ToArray();
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Path.GetFileName(directory);
                if (id.StartsWith('.')) continue;
                found.Add(id);
                try { await ProcessBaseAsync(id, directory, generation, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or ServiceException)
                {
                    _pending.Remove(id);
                    var message = ex is ServiceException service ? service.Message :
                        ex is InvalidDataException ? ex.Message : "资料读取失败，请检查文件格式、权限及处理限制；TXT/MD 需使用 UTF-8。";
                    _store.SetState(id, "error", message);
                    _logger.LogWarning("知识库 {Id} 处理失败，类型 {Type}。", id, ex.GetType().Name);
                }
            }
            foreach (var missing in _store.List().Where(s => !found.Contains(s.Id)))
            {
                _store.SetState(missing.Id, "missing", "知识库文件夹已移除，不再用于问答。");
                _pending.Remove(missing.Id);
            }
            Interlocked.Exchange(ref _validatedGeneration, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var state in _store.List()) _store.SetState(state.Id, "unavailable", "资料根目录不可访问；未删除历史索引。");
            Interlocked.Exchange(ref _validatedGeneration, -1);
            _watcher?.Dispose(); _watcher = null;
            _logger.LogWarning("资料根目录检查失败，类型 {Type}。", ex.GetType().Name);
        }
        finally { _scanGate.Release(); }
    }

    /// <summary>读取一套资料，复用未改变文件，只在全部成功后发布。</summary>
    /// <param name="id">一级目录名。</param>
    /// <param name="directory">服务配置根目录下的实际目录。</param>
    /// <param name="generation">本轮开始时的变动代号。</param>
    /// <param name="token">服务停止信号。</param>
    /// <returns>该知识库本轮处理完成后结束；首次发现变化时只登记等待稳定。</returns>
    private async Task ProcessBaseAsync(string id, string directory, long generation, CancellationToken token)
    {
        var files = new List<(string Relative, byte[] Bytes, string Hash)>();
        long totalBytes = 0;
        foreach (var path in EnumerateFiles(directory))
        {
            if (!DocumentReader.Supports(path)) continue;
            if (files.Count >= _options.MaxFilesPerBase) throw new InvalidDataException("支持的资料文件数超过配置上限。");
            if (new FileInfo(path).Length > _options.MaxFileBytes) throw new InvalidDataException("资料文件超过大小限制，请拆分文件。");
            // FileShare.Read 阻止本轮读取期间另一个写入者覆盖文件；失败后下一轮重试。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > _options.MaxFileBytes) throw new InvalidDataException("资料文件超过大小限制。");
            totalBytes += stream.Length;
            if (totalBytes > _options.MaxTotalFileBytes)
                throw new InvalidDataException("知识库原始文件总大小超过 MaxTotalFileBytes 限制，请拆分项目或减少资料。");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token);
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            files.Add((relative, bytes, DocumentReader.Hash(bytes)));
        }
        var embeddingIdentity = _options.UseEmbeddings ? await _model.EmbeddingIdentityAsync(token) : "off";
        // 解析规则或资源限制变化后必须重新核对，不能复用旧版解析结果绕过新限制。
        var pipeline = $"{DocumentReader.PipelineVersion}:{_options.ChunkCharacters}:limits={_options.MaxExpandedBytes},{_options.MaxExtractedCharacters},{_options.MaxPdfPages}:embedding={embeddingIdentity}";
        var fingerprint = DocumentReader.Hash(Encoding.UTF8.GetBytes(pipeline + "\n" + string.Join('\n', files.Select(f => f.Relative + ":" + f.Hash))));
        var previous = _store.ReadSnapshot(id);
        if (previous?.Fingerprint == fingerprint)
        {
            _pending.Remove(id);
            var current = _store.List().FirstOrDefault(b => b.Id == id);
            if (current?.Status is not ("ready" or "empty"))
                _store.SetState(id, previous.Documents.Count == 0 ? "empty" : "ready", previous.Documents.Count == 0 ? "没有支持的资料文件。" : "资料已核对，复用已有索引。");
            return;
        }
        _store.SetState(id, "updating", "资料发生变化，等待文件稳定并更新索引。");
        if (!_pending.TryGetValue(id, out var pending) || pending != fingerprint)
        { _pending[id] = fingerprint; return; }

        var documents = new List<KnowledgeDocument>();
        var count = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var reusable = previous?.Pipeline == pipeline
                ? previous.Documents.FirstOrDefault(d => d.RelativePath == file.Relative && d.Hash == file.Hash) : null;
            KnowledgeDocument document;
            try { document = reusable ?? DocumentReader.Read(id, file.Relative, file.Bytes, _options.ChunkCharacters, _options, token); }
            catch (InvalidDataException ex)
            {
                // 只返回知识库内相对路径，让操作员知道应修复哪份资料，不泄露服务器绝对路径。
                throw new InvalidDataException($"资料“{file.Relative}”：{ex.Message}", ex);
            }
            // 先检查总片段上限再调用模型，过大资料不会先消耗很长时间计算、最后才被拒绝。
            count += document.Chunks.Count;
            if (count > _options.MaxChunksPerBase) throw new InvalidDataException("知识段落数量超过配置上限。");
            if (reusable is null && _options.UseEmbeddings)
            {
                for (var offset = 0; offset < document.Chunks.Count; offset += _options.EmbeddingBatchSize)
                {
                    var batch = document.Chunks.Skip(offset).Take(_options.EmbeddingBatchSize).ToArray();
                    _store.SetState(id, "updating", $"正在生成检索索引：资料 {documents.Count + 1}/{files.Count}，片段 {offset + 1}～{offset + batch.Length}/{document.Chunks.Count}。");
                    // 文件名和章节一起参与语义理解，表格的字段值也有上下文；不用聊天模型改写原文。
                    var vectors = await _model.EmbedDocumentsAsync(batch.Select(chunk => chunk.Title + "\n" + chunk.Section + "\n" + chunk.Text).ToArray(), token);
                    if (vectors.Length != batch.Length) throw new ServiceException(502, "invalid_embedding", "资料片段与向量数量不一致，索引未发布。");
                    for (var i = 0; i < batch.Length; i++) document.Chunks[offset + i] = batch[i] with { Vector = vectors[i] };
                    // 一批完成即回到调度器，已经排队的前台请求可先于下一批执行。
                    await Task.Yield();
                }
            }
            documents.Add(document);
        }
        // 处理期间再次变动就放弃本次发布，让下一轮读取最新文件。
        if (Generation != generation) { _pending.Remove(id); return; }
        _store.Publish(new KnowledgeSnapshot(id, (previous?.Revision ?? 0) + 1, fingerprint, pipeline, documents));
        _pending.Remove(id);
        _logger.LogInformation("知识库 {Id} 已发布，共 {Count} 个文件。", id, documents.Count);
    }

    /// <summary>递归枚举文件，拒绝符号链接和目录联接，避免绕出指定资料目录。</summary>
    /// <param name="directory">当前扫描的真实目录。</param>
    /// <returns>排序后的文件路径。</returns>
    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        RejectLink(directory);
        foreach (var entry in Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            RejectLink(entry);
            if (Directory.Exists(entry))
            {
                if (Path.GetFileName(entry).StartsWith('.')) continue;
                foreach (var nested in EnumerateFiles(entry)) yield return nested;
            }
            else yield return entry;
        }
    }

    /// <summary>拒绝重新解析点；即使文件扩展名受支持也不能用链接读其他目录。</summary>
    /// <param name="path">待检查的文件或目录。</param>
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("资料中不支持符号链接或目录联接。");
    }

    /// <summary>监听变化但不在通知线程读取文件；失败由后续周期扫描补偿。</summary>
    private void EnsureWatcher()
    {
        if (_watcher is not null) return;
        _watcher = new FileSystemWatcher(_options.RootPath) { IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Error += (_, _) => Interlocked.Increment(ref _generation);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>收到文件通知时立即使旧目录核对状态失效。</summary>
    /// <param name="sender">文件监视器。</param>
    /// <param name="args">变动事件；实际文件内容留给扫描任务读取。</param>
    private void OnChanged(object sender, FileSystemEventArgs args) => Interlocked.Increment(ref _generation);

    /// <summary>服务停止后释放系统文件通知句柄。</summary>
    public override void Dispose() { _watcher?.Dispose(); base.Dispose(); }
}
