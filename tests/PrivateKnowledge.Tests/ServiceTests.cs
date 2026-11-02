// 用途：用虚构资料和明确的 Ollama 测试桩验证真实 HTTP、SQLite 与自动更新流程。
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PrivateKnowledge.Api.Contracts;
using Xunit;

namespace PrivateKnowledge.Tests;

/// <summary>端到端故障与资料隔离测试；测试桩只验证协议，不证明真实模型回答质量。</summary>
public sealed class ServiceTests
{
    /// <summary>提高单文件上限后，仍要限制同项目所有原始文件的总量。</summary>
    /// <returns>超过总量时暂停问答，移走多余资料后自动恢复的任务。</returns>
    [Fact]
    public async Task TotalSourceSizeLimitBlocksAndRecovers()
    {
        await using var fixture = await ServiceFixture.StartAsync(maxTotalFileBytes: 5000);
        var folder = Path.GetDirectoryName(fixture.AlphaFile)!;
        await File.WriteAllTextAsync(Path.Combine(folder, "资料一.txt"), new string('甲', 900));
        var extra = Path.Combine(folder, "资料二.txt");
        await File.WriteAllTextAsync(extra, new string('乙', 900));
        await fixture.WaitAsync("alpha", "error");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.PostAsync("alpha", "采集之前做什么？")).StatusCode);
        File.Delete(extra);
        await fixture.WaitAsync("alpha", "ready", 2);
        Assert.Equal("answered", (await fixture.AskAsync("alpha", "采集之前做什么？")).Status);
    }
    /// <summary>三种格式都必须经过真实扫描、SQLite、HTTP 引用、替换更新、重启复用和删除流程。</summary>
    /// <param name="extension">本版新支持的扩展名。</param>
    /// <returns>自动索引与问答协议验证任务；模型仍为明确测试桩。</returns>
    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    public async Task BinaryFormatsSupportAutomaticLifecycle(string extension)
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var before = await fixture.AskAsync("alpha", "采集之前做什么？");
        var path = Path.Combine(Path.GetDirectoryName(fixture.AlphaFile)!, "多格式." + extension);
        File.Delete(fixture.AlphaFile);
        await File.WriteAllBytesAsync(path, MakeFile(false));
        // Office 旁边的锁文件不是正式资料；即使内容不是合法压缩包，也不应让知识库失败。
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "~$多格式." + extension), "临时锁文件");
        await fixture.WaitAsync("alpha", "ready", before.KnowledgeRevision + 1);
        var question = extension == "pdf" ? "Channel acquisition workflow" : "设备连接成功后先做什么";
        var answer = await fixture.AskAsync("alpha", question);
        Assert.Equal("answered", answer.Status);
        Assert.NotEmpty(answer.Citations);
        Assert.All(answer.Citations, source => Assert.Equal("多格式." + extension, source.Title));
        if (extension == "pdf") Assert.All(answer.Citations, source => Assert.StartsWith("第 ", source.Section));
        if (extension == "xlsx") Assert.All(answer.Citations, source => Assert.Contains("工作表：操作规则 / 第 9 行", source.Section));
        var original = await fixture.Client.GetStringAsync("api/documents/" + answer.Citations[0].DocumentId);
        using (var json = JsonDocument.Parse(original))
            Assert.Contains(extension == "pdf" ? "Enable a channel" : "设备", json.RootElement.GetProperty("text").GetString());

        await fixture.RestartAsync();
        Assert.Equal(answer.KnowledgeRevision, (await fixture.AskAsync("alpha", question)).KnowledgeRevision);
        await File.WriteAllBytesAsync(path, MakeFile(true));
        await fixture.WaitAsync("alpha", "ready", answer.KnowledgeRevision + 1);
        var updated = await fixture.AskAsync("alpha", question);
        Assert.Contains(extension == "pdf" ? "channel settings" : "通道设置", updated.Answer);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("api/documents/" + answer.Citations[0].DocumentId)).StatusCode);
        File.Delete(path);
        await fixture.WaitAsync("alpha", "empty", updated.KnowledgeRevision + 1);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.PostAsync("alpha", question)).StatusCode);

        // 测试生成的是可解析的真实文件；内容只用于验证传递，不代表真实模型生成质量。
        byte[] MakeFile(bool changed) => extension switch
        {
            "docx" => DocumentFixtures.Word(changed ? "设备连接成功后先检查通道设置。" : "设备连接成功后先启用采集通道。"),
            "xlsx" => DocumentFixtures.Spreadsheet(changed ? "设备连接成功后先检查通道设置。" : "设备连接成功后先启用采集通道。"),
            _ => DocumentFixtures.Pdf(changed ? "Check channel settings before collecting data." : "Enable a channel before collecting data.")
        };
    }

    /// <summary>损坏的新格式文件会阻止本项目使用旧答案，修复后自动恢复，其他项目继续可用。</summary>
    /// <param name="extension">损坏文件的格式。</param>
    /// <returns>错误状态、项目隔离及修复验证任务。</returns>
    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pdf")]
    public async Task InvalidBinaryFileBlocksOnlyItsBaseAndRecovers(string extension)
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var path = Path.Combine(Path.GetDirectoryName(fixture.AlphaFile)!, "损坏文件." + extension);
        await File.WriteAllTextAsync(path, "这不是有效的二进制文件");
        await fixture.WaitAsync("alpha", "error");
        var states = await fixture.Client.GetStringAsync("api/knowledge-bases");
        using (var json = JsonDocument.Parse(states))
            Assert.Contains("损坏文件." + extension, json.RootElement.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "alpha").GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.PostAsync("alpha", "采集之前做什么？")).StatusCode);
        await fixture.WaitAsync("beta", "ready");
        Assert.Equal("answered", (await fixture.AskAsync("beta", "采集之后做什么？")).Status);
        var repaired = extension switch { "docx" => DocumentFixtures.Word(), "xlsx" => DocumentFixtures.Spreadsheet(), _ => DocumentFixtures.Pdf() };
        await File.WriteAllBytesAsync(path, repaired);
        await fixture.WaitAsync("alpha", "ready", 2);
        Assert.Equal("answered", (await fixture.AskAsync("alpha", "采集之前做什么？")).Status);
    }

    /// <summary>复现“新增人名后问是谁”：资料保存后必须进入新修订，问题才能交给模型。</summary>
    /// <returns>真实文件监视、索引发布和 HTTP 检索的验证任务。</returns>
    [Fact]
    public async Task SavedNewFactBecomesSearchableAndHealthShowsSourceDirectory()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        using var health = JsonDocument.Parse(await fixture.Client.GetStringAsync("health"));
        Assert.Equal(fixture.RootDirectory, health.RootElement.GetProperty("knowledgeRootPath").GetString());
        Assert.Equal(1, health.RootElement.GetProperty("scanSeconds").GetInt32());
        var before = await fixture.AskAsync("alpha", "采集之前做什么？");
        await File.WriteAllTextAsync(fixture.AlphaFile, "# 项目甲\n\n## 人员说明\n\n林晓舟是项目测试员，负责导入文件校验。\n");
        await fixture.WaitAsync("alpha", "updating");
        await fixture.WaitAsync("alpha", "ready", before.KnowledgeRevision + 1);
        var calls = fixture.ChatCalls;
        var answer = await fixture.AskAsync("alpha", "林晓舟是谁");
        Assert.Equal("answered", answer.Status);
        Assert.Contains("负责导入文件校验", answer.Answer);
        Assert.Equal(calls + 1, fixture.ChatCalls);
        Assert.NotEmpty(answer.Citations);
        Assert.True(answer.KnowledgeRevision > before.KnowledgeRevision);
        Assert.Equal("insufficient_evidence", (await fixture.AskAsync("alpha", "周映岚是谁")).Status);
        Assert.Equal(calls + 1, fixture.ChatCalls);
    }

    /// <summary>模型列表过滤云端别名和嵌入模型；选择跟随每个请求，省略仍使用默认值。</summary>
    /// <returns>真实 HTTP 协议与明确测试模型的验证任务。</returns>
    [Fact]
    public async Task ModelSelectionIsLocalAndPerRequest()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var catalog = await fixture.Client.GetFromJsonAsync<ModelCatalog>("api/models");
        Assert.Equal("fake-chat", catalog!.DefaultModel);
        Assert.Equal(new[] { "fake-chat", "fake-second" }, catalog.Models.Select(m => m.Name));
        using var selected = await fixture.Client.PostAsJsonAsync("api/questions", new QuestionRequest("alpha", "采集之前做什么？", "fake-second"));
        selected.EnsureSuccessStatusCode();
        Assert.Equal("fake-second", (await selected.Content.ReadFromJsonAsync<QuestionResponse>())!.Model);
        Assert.Equal("fake-second", fixture.LastChatModel);
        Assert.False(fixture.LastHasThink);
        Assert.Equal("fake-chat", (await fixture.AskAsync("alpha", "采集之前做什么？")).Model);
        Assert.True(fixture.LastHasThink);
        var calls = fixture.ChatCalls;
        foreach (var invalid in new[] { "missing", "fake-embed", "remote-alias", "" })
        {
            using var rejected = await fixture.Client.PostAsJsonAsync("api/questions", new QuestionRequest("alpha", "采集之前做什么？", invalid));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal("invalid_chat_model", (await rejected.Content.ReadFromJsonAsync<ApiError>())!.Code);
        }
        Assert.Equal(calls, fixture.ChatCalls);
        Assert.Null((await fixture.AskAsync("alpha", "火星轨道半径是多少？")).Model);
    }
    /// <summary>检查目录处理、项目隔离、未知问题、原文引用和增量更新。</summary>
    /// <returns>所有 HTTP 与资料断言完成后的任务。</returns>
    [Fact]
    public async Task FolderLifecycleAndIsolation()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var alpha = await fixture.AskAsync("alpha", "采集之前做什么？");
        Assert.Equal("answered", alpha.Status);
        Assert.Contains("先启用通道", alpha.Answer);
        Assert.DoesNotContain("先采集再启用通道", fixture.LastModelInput);
        var beta = await fixture.AskAsync("beta", "采集之后做什么？");
        Assert.Contains("先采集再启用通道", beta.Answer);
        Assert.DoesNotContain("先启用通道", fixture.LastModelInput);
        var calls = fixture.ChatCalls;
        var unknown = await fixture.AskAsync("alpha", "火星轨道半径是多少？");
        Assert.Equal("insufficient_evidence", unknown.Status);
        Assert.Empty(unknown.Citations);
        Assert.Equal(calls, fixture.ChatCalls);

        var documentId = alpha.Citations[0].DocumentId;
        var original = await fixture.Client.GetAsync("api/documents/" + documentId);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        await File.WriteAllTextAsync(fixture.AlphaFile, "# 操作\n\n## 采集前检查\n\n新版流程：采集前先检查通道设置。\n");
        await fixture.WaitAsync("alpha", "ready", alpha.KnowledgeRevision + 1);
        var updated = await fixture.AskAsync("alpha", "采集之前做什么？");
        Assert.Contains("检查通道设置", updated.Answer);
        Assert.DoesNotContain("先启用通道", updated.Answer);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("api/documents/" + documentId)).StatusCode);

        File.Delete(fixture.AlphaFile);
        await fixture.WaitAsync("alpha", "empty", updated.KnowledgeRevision + 1);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.PostAsync("alpha", "采集之前做什么？")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("api/documents/" + updated.Citations[0].DocumentId)).StatusCode);
        var invalid = await fixture.Client.PostAsJsonAsync("api/questions", new { knowledgeBaseId = "alpha", question = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    /// <summary>失去整个根目录不等于用户删除了全部知识，恢复后应复用原修订号。</summary>
    /// <returns>目录离线与恢复检查任务。</returns>
    [Fact]
    public async Task RootOutageDoesNotEraseSnapshot()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        var before = await fixture.AskAsync("alpha", "采集之前做什么？");
        var offline = Path.Combine(fixture.TestDirectory, "offline");
        // 两个目录都位于专用随机测试根目录，移动不会影响用户资料。
        Directory.Move(fixture.RootDirectory, offline);
        await fixture.WaitAsync("alpha", "unavailable");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.PostAsync("alpha", "采集之前做什么？")).StatusCode);
        Directory.Move(offline, fixture.RootDirectory);
        await fixture.WaitAsync("alpha", "ready");
        var after = await fixture.AskAsync("alpha", "采集之前做什么？");
        Assert.Equal(before.KnowledgeRevision, after.KnowledgeRevision);
    }

    /// <summary>乱码替换必须明确失败，不能在更新失败后仍使用旧文档回答。</summary>
    /// <returns>更新失败与修复检查任务。</returns>
    [Fact]
    public async Task InvalidUtf8BlocksOldAnswers()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        await File.WriteAllBytesAsync(fixture.AlphaFile, [0xFF, 0xFE, 0x00, 0x00]);
        await fixture.WaitAsync("alpha", "error");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.PostAsync("alpha", "采集之前做什么？")).StatusCode);
        await File.WriteAllTextAsync(fixture.AlphaFile, "# 修复\n\n采集前先确认采集通道。\n");
        await fixture.WaitAsync("alpha", "ready", 2);
        Assert.Contains("采集通道", (await fixture.AskAsync("alpha", "采集之前做什么？")).Answer);
    }

    /// <summary>模型错误、空答案、非法引用、超时都不能伪装成正常业务答案。</summary>
    /// <param name="mode">测试桩故障类型。</param>
    /// <param name="code">期望的 HTTP 状态码。</param>
    [Theory]
    [InlineData("error", 502)]
    [InlineData("empty", 502)]
    [InlineData("bad_citation", 502)]
    [InlineData("malformed", 502)]
    [InlineData("delay", 504)]
    public async Task ModelFailuresAreExplicit(string mode, int code)
    {
        await using var fixture = await ServiceFixture.StartAsync(timeoutSeconds: 1);
        fixture.Mode = mode;
        using var response = await fixture.PostAsync("alpha", "采集之前做什么？");
        Assert.Equal(code, (int)response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.False(string.IsNullOrWhiteSpace(error!.Code));
        Assert.DoesNotContain(fixture.TestDirectory, error.Message);
    }

    /// <summary>生成回答期间资料改变，旧回答必须被拒绝。</summary>
    /// <returns>并发更新检查任务。</returns>
    [Fact]
    public async Task ChangedKnowledgeRejectsInflightAnswer()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        fixture.Mode = "hold";
        var pending = fixture.PostAsync("alpha", "采集之前做什么？");
        await fixture.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await File.WriteAllTextAsync(fixture.AlphaFile, "# 新版\n\n采集前检查容量。\n");
        await fixture.WaitAsync("alpha", "ready", 2);
        fixture.ReleaseModel.TrySetResult();
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>可选语义模式生成向量，重启不重算未改文件；只更新一个文件时只重算该文件。</summary>
    /// <returns>向量缓存与增量处理检查任务。</returns>
    [Fact]
    public async Task EmbeddingsPersistAcrossRestart()
    {
        await using var fixture = await ServiceFixture.StartAsync(embeddings: true);
        var firstCalls = fixture.EmbedCalls;
        Assert.True(firstCalls >= 2);
        await fixture.RestartAsync();
        Assert.Equal(firstCalls, fixture.EmbedCalls);
        var answer = await fixture.AskAsync("alpha", "申请流程");
        Assert.Equal("answered", answer.Status);
        await File.WriteAllTextAsync(fixture.AlphaFile, "# 新说明\n\n采集前检查通道设置。\n");
        await fixture.WaitAsync("alpha", "ready", 2);
        Assert.Equal(firstCalls + 2, fixture.EmbedCalls); // 一次问题向量，加一次变更资料向量。
    }

    /// <summary>嵌入模型名称没变、实际文件已变时，旧向量也必须重建。</summary>
    /// <returns>模型摘要变更验证任务。</returns>
    [Fact]
    public async Task ChangedEmbeddingDigestRebuildsIndex()
    {
        await using var fixture = await ServiceFixture.StartAsync(embeddings: true);
        var count = fixture.EmbedCalls;
        fixture.EmbeddingDigest = "test-embed-v2";
        await fixture.WaitAsync("alpha", "ready", 2);
        await fixture.WaitAsync("beta", "ready", 2);
        Assert.Equal(count + 2, fixture.EmbedCalls);
    }

    /// <summary>过大的证据与问题必须明确拒绝，不能静默截断后继续生成。</summary>
    /// <returns>输入预算验证任务。</returns>
    [Fact]
    public async Task OversizedContextDoesNotReachModel()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        // 用一个完整、不可再按自然段拆小的候选验证超限；有其他小片段时允许预算选择器选小片段。
        await File.WriteAllTextAsync(fixture.AlphaFile, "# 采集\n\n采集" + new string('甲', 690));
        await fixture.WaitAsync("alpha", "ready", 2);
        using var response = await fixture.PostAsync("alpha", string.Concat(Enumerable.Repeat("采集", 500)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, fixture.ChatCalls);
    }

    /// <summary>客户端取消正在生成的请求后，后续问答仍能取得模型执行名额。</summary>
    /// <returns>取消和恢复验证任务。</returns>
    [Fact]
    public async Task CancellationDoesNotLeakModelSlot()
    {
        await using var fixture = await ServiceFixture.StartAsync();
        fixture.Mode = "hold";
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Client.PostAsJsonAsync("api/questions", new QuestionRequest("alpha", "采集之前做什么？"), cancellation.Token);
        await fixture.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        fixture.Mode = "normal";
        fixture.ReleaseModel.TrySetResult();
        Assert.Equal("answered", (await fixture.AskAsync("alpha", "采集之前做什么？")).Status);
    }
}

/// <summary>每个测试独享真实 HTTP 服务、SQLite 和临时目录；Ollama 使用明确的协议测试桩。</summary>
internal sealed class ServiceFixture : IAsyncDisposable
{
    /// <summary>本测试的随机根目录；只清理这个目录。</summary>
    public string TestDirectory { get; } = Path.Combine(Path.GetTempPath(), "private-knowledge-tests", Guid.NewGuid().ToString("N"));
    /// <summary>资料目录。</summary>
    public string RootDirectory => Path.Combine(TestDirectory, "knowledge");
    /// <summary>用来验证更新与删除的文件。</summary>
    public string AlphaFile => Path.Combine(RootDirectory, "alpha", "流程.md");
    /// <summary>测试 HTTP 客户端。</summary>
    public HttpClient Client { get; private set; } = null!;
    /// <summary>测试桩行为；normal 表示按传入原文构造固定回答。</summary>
    public string Mode { get; set; } = "normal";
    /// <summary>最近交给测试模型的资料，供隔离断言检查。</summary>
    public string LastModelInput { get; private set; } = "";
    /// <summary>记录测试桩实际收到的模型名称，验证客户端选择没有被忽略。</summary>
    public string LastChatModel { get; private set; } = "";
    /// <summary>普通聊天模型不应收到不支持的 think 参数。</summary>
    public bool LastHasThink { get; private set; }
    /// <summary>聊天接口调用次数。</summary>
    public int ChatCalls;
    /// <summary>嵌入接口调用次数。</summary>
    public int EmbedCalls;
    /// <summary>模拟同名嵌入模型的文件摘要变化。</summary>
    public string EmbeddingDigest { get; set; } = "test-embed-v1";
    /// <summary>通知测试代码：模型已经开始等待。</summary>
    public TaskCompletionSource ModelEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>由测试代码释放模型等待，模拟生成过程中发生资料更新。</summary>
    public TaskCompletionSource ReleaseModel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>测试模型 HTTP 应用。</summary>
    private WebApplication _fake = null!;
    /// <summary>实际被测服务。</summary>
    private WebApplication _service = null!;
    /// <summary>服务重启时复用的隔离配置。</summary>
    private string[] _arguments = [];

    /// <summary>创建虚构资料并启动真实本机端口，操作系统自动分配空闲端口。</summary>
    /// <param name="timeoutSeconds">模型超时配置，故障测试用 1 秒。</param>
    /// <param name="embeddings">是否验证可选语义检索流程。</param>
    /// <param name="maxTotalFileBytes">单项目原始资料总大小限制；资源边界测试使用较小值。</param>
    /// <param name="enterprise">验证企业密钥协议时开启，仍只监听测试机回环端口。</param>
    /// <returns>所有资料已就绪的测试环境。</returns>
    public static async Task<ServiceFixture> StartAsync(int timeoutSeconds = 15, bool embeddings = false, int maxTotalFileBytes = 104857600, bool enterprise = false)
    {
        var fixture = new ServiceFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.AlphaFile)!);
        Directory.CreateDirectory(Path.Combine(fixture.RootDirectory, "beta"));
        await File.WriteAllTextAsync(fixture.AlphaFile, "# 项目甲\n\n## 采集流程\n\n采集前先启用通道，再查看曲线。\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.RootDirectory, "beta", "流程.md"), "# 项目乙\n\n## 采集流程\n\n先采集再启用通道。\n");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        fixture._fake = builder.Build();
        fixture._fake.MapGet("/api/tags", () => Results.Json(new { models = new[] {
            new { name = "fake-chat", digest = "test-chat-v1" }, new { name = "fake-embed", digest = fixture.EmbeddingDigest },
            new { name = "fake-second", digest = "test-second-v1" }, new { name = "remote-alias", digest = "remote-v1" } } }));
        fixture._fake.MapPost("/api/show", async (HttpContext context) =>
        {
            using var json = await JsonDocument.ParseAsync(context.Request.Body);
            var name = json.RootElement.GetProperty("model").GetString();
            return Results.Json(new { capabilities = name == "fake-embed" ? new[] { "embedding" } :
                name == "fake-chat" ? new[] { "completion", "thinking" } : new[] { "completion" },
                remote_model = name == "remote-alias" ? "remote-model" : null });
        });
        fixture._fake.MapPost("/api/embed", async (HttpContext context) =>
        {
            Interlocked.Increment(ref fixture.EmbedCalls);
            using var input = await JsonDocument.ParseAsync(context.Request.Body);
            var count = input.RootElement.GetProperty("input").GetArrayLength();
            return Results.Json(new { embeddings = Enumerable.Range(0, count).Select(_ => new[] { 1.0f, 0.0f, 0.0f }).ToArray() });
        });
        fixture._fake.MapPost("/api/chat", async (HttpContext context) =>
        {
            Interlocked.Increment(ref fixture.ChatCalls);
            using var json = await JsonDocument.ParseAsync(context.Request.Body);
            fixture.LastChatModel = json.RootElement.GetProperty("model").GetString()!;
            fixture.LastHasThink = json.RootElement.TryGetProperty("think", out _);
            fixture.LastModelInput = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var input = JsonDocument.Parse(fixture.LastModelInput);
            if (fixture.Mode == "error") return Results.StatusCode(500);
            if (fixture.Mode == "malformed") return Results.Text("not json");
            if (fixture.Mode == "delay") await Task.Delay(3000, context.RequestAborted);
            if (fixture.Mode == "hold")
            {
                fixture.ModelEntered.TrySetResult();
                await fixture.ReleaseModel.Task.WaitAsync(context.RequestAborted);
            }
            var text = input.RootElement.GetProperty("sources")[0].GetProperty("text").GetString();
            var content = JsonSerializer.Serialize(new { status = "answered", answer = fixture.Mode == "empty" ? "" : "测试桩回答：" + text,
                sourceIds = new[] { fixture.Mode == "bad_citation" ? "S999" : "S1" } });
            return Results.Json(new { done = true, done_reason = "stop", message = new { content } });
        });
        await fixture._fake.StartAsync();
        fixture._arguments = ["--urls", "http://127.0.0.1:0", "--Knowledge:RootPath", fixture.RootDirectory,
            "--Knowledge:DataPath", Path.Combine(fixture.TestDirectory, "data"), "--Knowledge:ScanSeconds", "1",
            "--Knowledge:MaxTotalFileBytes", maxTotalFileBytes.ToString(),
            "--Knowledge:UseEmbeddings", embeddings.ToString(), "--Model:BaseUrl", Address(fixture._fake),
            "--Model:ChatModel", "fake-chat", "--Model:EmbeddingModel", "fake-embed", "--Model:TimeoutSeconds", timeoutSeconds.ToString()];
        if (enterprise) fixture._arguments = [.. fixture._arguments, "--Deployment:Mode", "Enterprise", "--Deployment:ApiKey", "test-only-enterprise-key-1234567890"];
        await fixture.StartServiceAsync();
        return fixture;
    }

    /// <summary>启动服务并等待两套资料都就绪。</summary>
    private async Task StartServiceAsync()
    {
        // 自动测试不依赖公网；动态目录另有真实官网的主动验收脚本。
        _service = PrivateKnowledge.Api.Program.CreateApp(_arguments, services =>
            services.AddSingleton<PrivateKnowledge.Api.Models.IModelLibrary>(new TestModelLibrary()));
        await _service.StartAsync();
        Client = new HttpClient { BaseAddress = new Uri(Address(_service)), Timeout = TimeSpan.FromSeconds(20) };
        if (_arguments.Contains("Enterprise")) Client.DefaultRequestHeaders.Add("X-Knowledge-Key", "test-only-enterprise-key-1234567890");
        await WaitAsync("alpha", "ready");
        await WaitAsync("beta", "ready");
    }

    /// <summary>保留资料和 SQLite 重启，验证缓存可恢复。</summary>
    public async Task RestartAsync()
    {
        Client.Dispose();
        await _service.StopAsync(); await _service.DisposeAsync();
        await StartServiceAsync();
    }

    /// <summary>发送问答请求，保留 HTTP 状态供故障断言检查。</summary>
    /// <param name="id">例如 alpha。</param>
    /// <param name="question">测试问题。</param>
    /// <returns>调用者负责释放的响应。</returns>
    public Task<HttpResponseMessage> PostAsync(string id, string question) => Client.PostAsJsonAsync("api/questions", new QuestionRequest(id, question));

    /// <summary>发送期望成功的请求并读取业务回答。</summary>
    /// <param name="id">知识库标识。</param>
    /// <param name="question">完整问题。</param>
    /// <returns>反序列化后的响应对象。</returns>
    public async Task<QuestionResponse> AskAsync(string id, string question)
    {
        using var response = await PostAsync(id, question);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<QuestionResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>轮询可观察状态，避免依靠固定长等待猜测索引是否完成。</summary>
    /// <param name="id">知识库标识。</param>
    /// <param name="status">期待的状态。</param>
    /// <param name="revision">期待至少达到的修订号。</param>
    /// <returns>满足条件后完成，否则在超时时给出最后状态。</returns>
    public async Task WaitAsync(string id, string status, long revision = 0)
    {
        var last = "";
        // 文件夹恢复时，单个项目可能先恢复 ready，根目录通知还在排队。
        // 连续两次确认目录也已核对，才让下一条问答开始；不把合法的保护性 503 当作产品故障。
        var readyChecks = 0;
        for (var i = 0; i < 100; i++)
        {
            last = await Client.GetStringAsync("api/knowledge-bases");
            using var json = JsonDocument.Parse(last);
            var matched = json.RootElement.EnumerateArray().Any(b => b.GetProperty("id").GetString() == id &&
                b.GetProperty("status").GetString() == status && b.GetProperty("revision").GetInt64() >= revision);
            if (matched && status != "ready") return;
            if (matched)
            {
                using var healthResponse = await Client.GetAsync("health");
                using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
                if (health.RootElement.GetProperty("catalog").GetString() == "ready")
                { if (++readyChecks >= 2) return; }
                else readyChecks = 0;
            }
            else readyChecks = 0;
            await Task.Delay(100);
        }
        Assert.True(false, "等待知识库状态超时：" + last);
    }

    /// <summary>读取 Kestrel 实际分配的端口。</summary>
    /// <param name="app">已启动的测试应用。</param>
    /// <returns>以斜杠结束的本机地址。</returns>
    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/') + "/";

    /// <summary>关闭两个服务后，只删除本测试随机目录；共享根目录和用户目录都不会删除。</summary>
    public async ValueTask DisposeAsync()
    {
        ReleaseModel.TrySetResult();
        Client?.Dispose();
        if (_service is not null) { await _service.StopAsync(); await _service.DisposeAsync(); }
        if (_fake is not null) { await _fake.StopAsync(); await _fake.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var allowedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "private-knowledge-tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(TestDirectory);
        if (!target.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("拒绝删除测试范围之外的目录。");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
