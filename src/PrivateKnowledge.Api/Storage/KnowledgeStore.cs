// 用途：用单个 SQLite 文件持久保存资料快照和状态，重启不需要重新生成全部索引。
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using PrivateKnowledge.Api.Configuration;
using PrivateKnowledge.Api.Contracts;

namespace PrivateKnowledge.Api.Storage;

/// <summary>小规模本机知识库存储；快照以 JSON 保存，其中包括原文、段落和可选向量。</summary>
public sealed class KnowledgeStore
{
    /// <summary>SQLite 连接信息；每次操作新开短连接，避免长期占用文件锁。</summary>
    private readonly string _connectionString;

    /// <summary>创建数据库结构；目录来自已验证的部署配置。</summary>
    /// <param name="options">例如 DataPath 指向发布包外的私有数据目录。</param>
    public KnowledgeStore(KnowledgeOptions options)
    {
        Directory.CreateDirectory(options.DataPath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.DataPath, "knowledge.db"), DefaultTimeout = 10 }.ToString();
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode=WAL;");
        connection.Execute("""
            CREATE TABLE IF NOT EXISTS KnowledgeBases (
                Id TEXT PRIMARY KEY, Status TEXT NOT NULL, Revision INTEGER NOT NULL DEFAULT 0,
                Fingerprint TEXT NOT NULL DEFAULT '', Message TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                DocumentCount INTEGER NOT NULL DEFAULT 0, SnapshotJson TEXT NULL);
            """);
    }

    /// <summary>打开短连接，调用者必须用 using 释放。</summary>
    /// <returns>已连接到本机知识数据库的连接。</returns>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>返回所有已发现知识库及状态，包含失败或已删除的知识库。</summary>
    /// <returns>不含原文和服务器路径的状态列表。</returns>
    public List<BaseState> List()
    {
        using var connection = Open();
        return connection.Query<BaseState>("SELECT Id,Status,Revision,Fingerprint,Message,UpdatedAt,DocumentCount FROM KnowledgeBases ORDER BY Id").ToList();
    }

    /// <summary>读取已发布快照；调用者仍须检查状态是否为 ready。</summary>
    /// <param name="id">知识库目录名，例如 demo-a。</param>
    /// <returns>历史快照或 null；更新失败时返回历史快照只用于增量处理，不能用于新问答。</returns>
    public KnowledgeSnapshot? ReadSnapshot(string id)
    {
        using var connection = Open();
        var json = connection.QuerySingleOrDefault<string>("SELECT SnapshotJson FROM KnowledgeBases WHERE Id=@id", new { id });
        return json is null ? null : JsonSerializer.Deserialize<KnowledgeSnapshot>(json);
    }

    /// <summary>保存处理状态，保留旧快照以便复用未改变的文档。</summary>
    /// <param name="id">知识库标识。</param>
    /// <param name="status">例如 updating 或 error。</param>
    /// <param name="message">安全的中文处理说明。</param>
    public void SetState(string id, string status, string message)
    {
        using var connection = Open();
        connection.Execute("""
            INSERT INTO KnowledgeBases(Id,Status,Message,UpdatedAt) VALUES(@id,@status,@message,@now)
            ON CONFLICT(Id) DO UPDATE SET Status=@status, Message=@message, UpdatedAt=@now;
            """, new { id, status, message, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    /// <summary>一次 SQL 原子替换完整快照，读者不会看到只写入一半的数据。</summary>
    /// <param name="snapshot">全部文件成功处理后的新快照。</param>
    public void Publish(KnowledgeSnapshot snapshot)
    {
        using var connection = Open();
        connection.Execute("""
            UPDATE KnowledgeBases SET Status=@status,Revision=@Revision,Fingerprint=@Fingerprint,
                SnapshotJson=@json,DocumentCount=@count,Message=@message,UpdatedAt=@now WHERE Id=@Id;
            """, new { snapshot.Id, snapshot.Revision, snapshot.Fingerprint,
            json = JsonSerializer.Serialize(snapshot), count = snapshot.Documents.Count,
            status = snapshot.Documents.Count == 0 ? "empty" : "ready",
            message = snapshot.Documents.Count == 0 ? "没有支持的资料文件。" : "资料已就绪。",
            now = DateTimeOffset.UtcNow.ToString("O") });
    }
}
