// 用途：用 30 个虚构知识问题比较关键词和混合检索，真实调用本机模型，并记录答案、引用、耗时和抽样资源峰值。
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Models;
using PrivateKnowledge.Api.Retrieval;
using PrivateKnowledge.Api.Storage;

namespace PrivateKnowledge.SemanticChecks;

/// <summary>可重复运行的真实模型验收；退出码非零表示有失败或没有完成。</summary>
internal static class Program
{
    /// <summary>报告使用可阅读中文，报告仅含本工具生成的虚构资料。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>创建独立目录、启动临时服务，先比较检索再走真实问答接口。</summary>
    /// <param name="args">第一个参数为项目根目录；第二个可传 --retrieval-only 只评估检索；第三个可传语义门槛。</param>
    /// <returns>全部通过为 0，任一问题失败为 1。</returns>
    public static async Task<int> Main(string[] args)
    {
        var projectRoot = Path.GetFullPath(args[0]);
        var output = Path.Combine(projectRoot, "TestResults", "semantic-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var root = Path.Combine(output, "knowledge");
        Directory.CreateDirectory(Path.Combine(root, "alpha"));
        Directory.CreateDirectory(Path.Combine(root, "beta"));
        await WriteFixturesAsync(root);
        var threshold = args.Length > 2 ? args[2] : "0.45";
        var chatModel = args.Length > 3 ? args[3] : "qwen3:1.7b";
        await using var app = PrivateKnowledge.Api.Program.CreateApp([
            "--urls", "http://127.0.0.1:0", "--Knowledge:RootPath", root, "--Knowledge:DataPath", Path.Combine(output, "data"),
            "--Knowledge:ScanSeconds", "1", "--Knowledge:ChunkCharacters", "700", "--Knowledge:UseEmbeddings", "true",
            "--Knowledge:MinimumSemanticScore", threshold, "--Model:EmbeddingModel", "qwen3-embedding:0.6b",
            "--Model:EmbeddingOnCpu", "true", "--Model:EmbeddingKeepAliveSeconds", "300", "--Model:ChatKeepAliveSeconds", "300",
            "--Model:ChatModel", chatModel, "--Model:TimeoutSeconds", "120"],
            services => services.AddLogging(logging => logging.ClearProviders()));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(130) };
        using var samplerStop = new CancellationTokenSource();
        var resources = new ResourceSamples();
        var sampling = resources.SampleAsync(samplerStop.Token);
        var startup = Stopwatch.StartNew();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        client.BaseAddress = new Uri(address);
        var reports = new List<object>();
        var failed = 0;
        try
        {
            var store = app.Services.GetRequiredService<KnowledgeStore>();
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (store.List().Count(item => item.Status == "ready") < 2)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("独立索引未就绪：" + JsonSerializer.Serialize(store.List()));
                await Task.Delay(300);
            }
            var indexSeconds = startup.Elapsed.TotalSeconds;
            var snapshot = store.ReadSnapshot("alpha")!;
            var hybrid = app.Services.GetRequiredService<Retriever>();
            var baseline = new Retriever(new KnowledgeOptions { ChunkCharacters = 700 }, app.Services.GetRequiredService<IModelClient>());
            // 可单独比较已有回答模型的边界案例，不用为诊断一个失败重复跑所有题。
            var cases = args.Contains("--critical") ? Cases().Where((item, index) => new[] { 7, 8, 12, 16, 23, 27 }.Contains(index)).ToArray() : Cases();
            foreach (var item in cases)
            {
                var clock = Stopwatch.StartNew();
                var before = await baseline.SearchAsync(snapshot, item.Question, default);
                var after = await hybrid.SearchAsync(snapshot, item.Question, default);
                var retrievalSeconds = clock.Elapsed.TotalSeconds;
                var expected = item.Expected;
                var beforeFound = expected is not null && before.Any(chunk => chunk.Text.Contains(expected, StringComparison.Ordinal));
                var afterFound = expected is not null && after.Any(chunk => chunk.Text.Contains(expected, StringComparison.Ordinal));
                var allScores = new List<object>();
                // 仅测试工具记录候选余弦分数，便于校准；相似度不是答案正确率。
                var query = await app.Services.GetRequiredService<IModelClient>().EmbedQueryAsync(item.Question, default);
                foreach (var chunk in snapshot.Documents.SelectMany(document => document.Chunks))
                    allScores.Add(new { chunk.Title, chunk.Section, score = Dot(query, chunk.Vector!), matchesExpected = expected is not null && chunk.Text.Contains(expected) });
                QuestionResponse? answer = null;
                var answerSeconds = 0.0;
                var passed = expected is null || afterFound;
                string? error = null;
                if (!args.Contains("--retrieval-only"))
                {
                    clock.Restart();
                    using var response = await client.PostAsJsonAsync("api/questions", new QuestionRequest("alpha", item.Question));
                    if (response.IsSuccessStatusCode)
                    {
                        answer = await response.Content.ReadFromJsonAsync<QuestionResponse>();
                        passed = expected is null ? answer!.Status == "insufficient_evidence" && answer.Citations.Count == 0 :
                            afterFound && answer!.Status == "answered" && AnswerMatches(answer.Answer, expected) && answer.Citations.Any(citation => citation.Excerpt.Contains(expected));
                    }
                    else { passed = false; error = await response.Content.ReadAsStringAsync(); }
                    answerSeconds = clock.Elapsed.TotalSeconds;
                }
                if (!passed) failed++;
                reports.Add(new { item.Question, expected, beforeFound, afterFound, candidateCount = after.Count, retrievalSeconds, answerSeconds, passed, answer, error, scores = allScores });
                Console.WriteLine($"{reports.Count:00} keyword={beforeFound} hybrid={afterFound} status={answer?.Status ?? "retrieval"} pass={passed} {item.Question}");
                // 每题保存一次，外部中断时也能查到已完成的真实结果。
                await File.WriteAllTextAsync(Path.Combine(output, "questions.json"), JsonSerializer.Serialize(reports, JsonOptions));
            }
            // 再按生产默认值验证：CPU 计算完成后释放嵌入模型，而不是仅凭配置声称释放。
            using var lowMemory = new OllamaClient(new ModelOptions { EmbeddingModel = "qwen3-embedding:0.6b", EmbeddingOnCpu = true, EmbeddingKeepAliveSeconds = 0 }, new KnowledgeOptions { UseEmbeddings = true });
            var releaseClock = Stopwatch.StartNew();
            await lowMemory.EmbedQueryAsync("释放检索模型检查", default);
            await Task.Delay(300);
            using var ps = await client.GetAsync("health");
            using var ollama = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/") };
            var residents = await ollama.GetStringAsync("api/ps");
            using var residentJson = JsonDocument.Parse(residents);
            var released = !residentJson.RootElement.GetProperty("models").EnumerateArray().Any(model => model.GetProperty("name").GetString() == "qwen3-embedding:0.6b");
            if (!released) failed++;
            var summary = new { total = reports.Count, failed, threshold, chatModel, indexSeconds, cpuEmbeddingReleaseSeconds = releaseClock.Elapsed.TotalSeconds,
                embeddingReleased = released, resources.PeakOllamaWorkingSetMiB, resources.PeakLoadedVramMiB,
                resourceNote = "每 500 毫秒抽样 Ollama 进程工作集和 api/ps 的模型显存；不是整机内存、独占增量或严格峰值。首次新建索引，评估阶段保留模型 300 秒，最后单独检查生产默认释放策略。" };
            await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, JsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
            Console.WriteLine("REPORT=" + output);
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            samplerStop.Cancel();
            await sampling;
            await app.StopAsync();
        }
    }

    /// <summary>生成与实际使用类似的虚构表格、手册、故障说明和另一项目的不同流程。</summary>
    /// <param name="root">本次独立资料目录。</param><returns>写入完成任务。</returns>
    private static async Task WriteFixturesAsync(string root)
    {
        var docs = new Dictionary<string, string>
        {
            ["通道与曲线.md"] = "# 虚构温度监控软件\n\n## 基础信息\n\n| 项目 | 示例规则 |\n| --- | --- |\n| 通道编号 | CH1 至 CH8 |\n| 连接状态 | 仅表示通信已建立 |\n| 曲线显示 | 勾选曲线图例 |\n| 采样间隔单位 | 秒 |\n| 上下限来源 | 工艺文件 |\n| 记录目录 | 需具有写入权限 |\n",
            ["操作流程.md"] = "# 虚构温度监控操作指南\n\n## 显示曲线\n\n先连接设备，再到通道设置启用所需通道，回到实时监控点击开始采集，最后在曲线图例勾选要显示的通道。\n\n## 历史记录\n\n实时曲线不自动保存。点击开始记录建立记录任务后，采集值才会写入历史文件。查询历史时选择任务、时间范围和通道，确认结果后点击导出。\n\n## 报警边界\n\n越界时显示报警并记录事件，软件不会自动停止加热设备。实际报警上下限由工艺文件提供。",
            ["故障指南.md"] = "# 虚构温度监控故障指南\n\n## E101\n\n错误码 E101 表示采集设备通信超时，检查连接状态和通信设置。\n\n## E203\n\n错误码 E203 表示没有启用任何采集通道，先到通道设置启用通道。\n\n## E307\n\n错误码 E307 表示记录目录不可写，检查目录和账号写入权限。"
        };
        foreach (var pair in docs) await File.WriteAllTextAsync(Path.Combine(root, "alpha", pair.Key), pair.Value);
        await File.WriteAllTextAsync(Path.Combine(root, "beta", "点检记录.md"), "# 虚构设备点检工具\n\n选择设备编号，填写点检结果，保存草稿，确认后提交记录。已提交记录须由管理员退回后修改。本工具不读取温度曲线。\n");
    }

    /// <summary>20 个有依据问题与 10 个应拒答问题；预期答案只存在于测试工具，不进入产品代码。</summary>
    /// <returns>固定评估集，含同义问法、表格字段、顺序、故障与跨知识库隔离。</returns>
    private static (string Question, string? Expected)[] Cases() =>
    [
        ("连接上设备为何没有温度曲线", "开始采集"), ("设备已连接但画面没有曲线，下一步是什么", "开始采集"),
        ("通道一共有哪些编号", "CH1 至 CH8"), ("连接状态说明温度已经开始采集吗", "仅表示通信已建立"),
        ("要在图上看到某路曲线还要做什么", "勾选曲线图例"), ("采样间隔的单位是什么", "秒"),
        ("报警上下限由什么资料提供", "工艺文件"), ("记录目录需要什么权限", "写入权限"),
        ("连接设备后先去哪里启用通道", "通道设置"), ("开始采集之后如何选择显示的线", "曲线图例"),
        ("实时曲线会自动保存吗", "不自动保存"), ("怎样新建历史记录任务", "开始记录"),
        ("查询历史需要选择什么时间信息", "时间范围"), ("查询完后如何获取文件", "导出"),
        ("报警出现后加热设备会自动停止吗", "不会自动停止"), ("遇到 E101 应检查什么", "通信设置"),
        ("E203 表示什么", "没有启用任何采集通道"), ("E203 需要到哪里处理", "通道设置"),
        ("E307 提示什么故障", "记录目录不可写"), ("E307 要检查哪种权限", "写入权限"),
        ("我的身份证号码是什么", null), ("这台设备合格温度是多少度", null),
        ("点检工具提交记录的方法是什么", null), ("软件管理员的手机号码是多少", null),
        ("工艺文件规定的上限是 80 度吗", null), ("今天外面会下雨吗", null),
        ("明年的设备采购预算是多少", null), ("错误码 E999 怎么处理", null),
        ("真实车间设备的 IP 地址是多少", null), ("采样间隔默认一定是几秒", null)
    ];

    /// <summary>计算验收诊断分数，使用双精度累计；仅用于报告，不影响产品排序。</summary>
    /// <param name="a">问题向量。</param><param name="b">资料向量。</param><returns>余弦相似度。</returns>
    private static double Dot(float[] a, float[] b)
    {
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { dot += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i]; }
        return dot / Math.Sqrt(aa * bb);
    }

    /// <summary>允许明确的同义否定表述，避免把“不会自动停止”换一种说法误判成模型错误；不使用另一个模型自动打分。</summary>
    /// <param name="answer">真实模型回答。</param><param name="expected">本题的原文字段。</param><returns>是否满足手写答案检查。</returns>
    private static bool AnswerMatches(string answer, string expected) => expected == "不会自动停止"
        ? System.Text.RegularExpressions.Regex.IsMatch(answer, "不会自动停止|不会自动停机|不负责停机|不能自动停机") : answer.Contains(expected);
}

/// <summary>抽样而不是严格监控；Ollama 可能同时被其他程序使用，因此报告不声称是本功能独占资源。</summary>
internal sealed class ResourceSamples
{
    /// <summary>抽样发现的 Ollama 进程工作集之和最大值。</summary>
    public double PeakOllamaWorkingSetMiB { get; private set; }
    /// <summary>Ollama 返回的所有驻留模型显存总量最大值。</summary>
    public double PeakLoadedVramMiB { get; private set; }
    /// <summary>每半秒抽样；模型服务短暂不可用时跳过，不干扰问答验收。</summary>
    /// <param name="token">验收结束的取消信号。</param><returns>采样结束任务。</returns>
    public async Task SampleAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            while (!token.IsCancellationRequested)
            {
                long workingSet = 0;
                foreach (var process in Process.GetProcessesByName("ollama").Concat(Process.GetProcessesByName("llama-server")))
                    using (process) { try { workingSet += process.WorkingSet64; } catch (InvalidOperationException) { } }
                PeakOllamaWorkingSetMiB = Math.Max(PeakOllamaWorkingSetMiB, workingSet / 1048576.0);
                try
                {
                    using var json = JsonDocument.Parse(await client.GetStringAsync("http://127.0.0.1:11434/api/ps", token));
                    var used = json.RootElement.GetProperty("models").EnumerateArray().Sum(model => model.GetProperty("size_vram").GetInt64());
                    PeakLoadedVramMiB = Math.Max(PeakLoadedVramMiB, used / 1048576.0);
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
