using LiteDB;

namespace Hime.Data;

/// <summary>
/// Hime 数据库会话（类似 EF Core DbContext）
/// </summary>
public class HimeDbContext : IDisposable
{
    private readonly LiteDatabase _database;
    private bool _disposed;

    /// <summary>
    /// 暴露底层 LiteDatabase，供需要直接 GetCollection 的服务使用
    /// </summary>
    public LiteDatabase Database => _database;

    /// <summary>
    /// 创建 DbContext，数据库文件存放在程序运行目录的 data 文件夹中
    /// </summary>
    /// <param name="databaseName">数据库文件名（不含扩展名），默认 hime</param>
    public HimeDbContext(string databaseName = "hime")
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

        var dbPath = Path.Combine(dataDir, $"{databaseName}.db");
        _database = new LiteDatabase($"Filename={dbPath};Connection=shared");

    }

    /// <summary>
    /// 手动刷新写入磁盘
    /// </summary>
    public void SaveChanges()
    {
        _database.Checkpoint();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _database.Dispose();
        }
        _disposed = true;
    }
}