// 用途：生成虚构的三种格式资料，启动隔离服务并调用真实 Ollama，记录实际结果；不修改日常知识库。
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PrivateKnowledge.Api.Contracts;
using PrivateKnowledge.Api.Knowledge;
using PrivateKnowledge.Tests;

namespace PrivateKnowledge.FormatChecks;

/// <summary>手动运行的完整验证工具；需要已运行的本地 Ollama 和已安装的聊天模型。</summary>
public static class Program
{
    /// <summary>在 TestResults 下建立专用目录，依次验证三种资料的提取、问答和出处。</summary>
    /// <param name="args">第一个参数可指定本地模型，第二个参数可指定中文 PDF；默认使用 qwen3:1.7b 和随工具提供的虚构 PDF。</param>
    /// <returns>全部通过返回 0；任何格式未回答或出处不正确时抛出异常，进程返回非零。</returns>
    public static async Task<int> Main(string[] args)
    {
        var model = args.Length > 0 ? args[0] : "qwen3:1.7b";
        var pdfPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "Samples", "中文操作指南.pdf");
        var directory = Path.GetFullPath(Path.Combine("TestResults", "format-smoke", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
        var root = Path.Combine(directory, "knowledge");
        var instruction = "设备连接成功后，下一步先启用采集通道。";
        var samples = new Dictionary<string, byte[]>
        {
            ["docx"] = DocumentFixtures.Word(instruction),
            ["xlsx"] = DocumentFixtures.Spreadsheet(instruction),
            ["pdf"] = await File.ReadAllBytesAsync(pdfPath)
        };
        foreach (var sample in samples)
        {
            var folder = Path.Combine(root, sample.Key);
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, "操作指南." + sample.Key), sample.Value);
            var extracted = DocumentReader.Read(sample.Key, "操作指南." + sample.Key, sample.Value, 1400);
            if (!extracted.Text.Contains(instruction)) throw new InvalidDataException(sample.Key + " 中文提取不完整。");
        }

        // 显式覆盖资料路径和数据库路径；不会读取、更新用户平时使用的 SampleKnowledge/data。
        await using var app = PrivateKnowledge.Api.Program.CreateApp(["--urls", "http://127.0.0.1:0",
            "--Knowledge:RootPath", root, "--Knowledge:DataPath", Path.Combine(directory, "data"),
            "--Knowledge:ScanSeconds", "1", "--Model:ChatModel", model, "--Model:TimeoutSeconds", "120"]);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(130) };
            await WaitReadyAsync(client);
            var answers = new List<QuestionResponse>();
            foreach (var format in samples.Keys)
            {
                using var response = await client.PostAsJsonAsync("api/questions", new QuestionRequest(format, "设备连接成功后，下一步要做什么？", model));
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(format + " HTTP 失败：" + body);
                var answer = JsonSerializer.Deserialize<QuestionResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                if (answer.Status != "answered" || !answer.Answer.Contains("采集通道") || answer.Model != model ||
                    answer.Citations.Count == 0 || answer.Citations.Any(c => c.Title != "操作指南." + format))
                    throw new InvalidOperationException(format + " 回答或出处验证失败：" + body);
                answers.Add(answer);
                Console.WriteLine($"FORMAT_PASS={format}; MODEL={answer.Model}; SOURCE={answer.Citations[0].Section}; ANSWER={answer.Answer}");
            }
            var report = new { checkedAtUtc = DateTime.UtcNow, source = "真实本地 Ollama；全部资料为虚构测试内容", model, root, answers };
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("FORMAT_REPORT=" + Path.Combine(directory, "result.json"));
            return 0;
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>等待资料扫描稳定且本地模型就绪；出错时保留最后状态便于排查。</summary>
    /// <param name="client">连接到本工具创建的隔离服务。</param>
    /// <returns>三个知识库均 ready 且目录核对完成后结束。</returns>
    private static async Task WaitReadyAsync(HttpClient client)
    {
        var last = "";
        for (var i = 0; i < 100; i++)
        {
            using var response = await client.GetAsync("health");
            last = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(last);
            if (response.IsSuccessStatusCode && json.RootElement.GetProperty("catalog").GetString() == "ready" &&
                json.RootElement.GetProperty("knowledgeBases").EnumerateArray().Count(b => b.GetProperty("status").GetString() == "ready") == 3) return;
            await Task.Delay(200);
        }
        throw new InvalidOperationException("资料或模型未就绪：" + last);
    }
}
