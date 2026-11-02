// 用途：只保存连接偏好，不把私有问题、答案和引用写到磁盘。
using System.Text.Json;

namespace PrivateKnowledge.Desktop.Services;

/// <summary>客户端重启时恢复的少量设置。</summary>
/// <param name="ServiceAddress">服务根地址，例如 http://127.0.0.1:5088/。</param>
/// <param name="LastKnowledgeBaseId">上次选择的目录名，例如 demo-a。</param>
/// <param name="Mode">Local 自动准备本机；Enterprise 只连接 IT 提供的服务。</param>
/// <param name="KnowledgePath">本机资料目录；空值表示沿用服务现有配置。</param>
/// <param name="EnterpriseAddress">单独记住企业地址，切换模式不会丢失本机地址。</param>
/// <param name="LocalAddress">个人模式使用的地址；旧设置为空时沿用原 ServiceAddress。</param>
/// <param name="SetupCompleted">true 已完成首次配置；false 下次继续引导；null 兼容旧版已有配置。</param>
/// <param name="PreferredModel">首次配置确认的回答模型；用于默认模型缺失但选择了已有模型的情况。</param>
public sealed record ClientSettings(string ServiceAddress = "http://127.0.0.1:5088/", string? LastKnowledgeBaseId = null,
    string Mode = "Local", string? KnowledgePath = null, string EnterpriseAddress = "", string? LocalAddress = null, bool? SetupCompleted = null, string? PreferredModel = null);

/// <summary>保存到当前用户配置目录；软件本体仍留在 D 盘项目目录。</summary>
public sealed class SettingsStore
{
    /// <summary>设置文件绝对路径；可在测试中指定临时目录。</summary>
    public string FilePath { get; }

    /// <summary>确定设置文件位置，不立即创建文件。</summary>
    /// <param name="filePath">测试传入隔离路径；不填写时先取 PRIVATEKNOWLEDGE_SETTINGS_PATH 环境变量，最后使用用户 LocalApplicationData。</param>
    public SettingsStore(string? filePath = null) => FilePath = filePath ?? Environment.GetEnvironmentVariable("PRIVATEKNOWLEDGE_SETTINGS_PATH") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateKnowledge.Desktop", "settings.json");

    /// <summary>读取已有设置，第一次使用时返回本机默认连接。</summary>
    /// <returns>保存的连接与项目；损坏文件抛出异常，由界面提示并使用默认值。</returns>
    public ClientSettings Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath)) ?? throw new JsonException("设置文件为空。")
        : new ClientSettings();

    /// <summary>先写临时文件再替换，降低中途退出造成半份 JSON 的概率。</summary>
    /// <param name="settings">已校验的地址与当前项目，不含聊天记录。</param>
    public void Save(ClientSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
