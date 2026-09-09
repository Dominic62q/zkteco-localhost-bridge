using Microsoft.Data.Sqlite;

namespace Bridge.Data;

/// <summary>Idempotent SQLite initializer. DB lives in ../data/, never in git.</summary>
public static class Db
{
    public static string PathFor(string contentRoot)
        => Path.GetFullPath(Path.Combine(contentRoot, "..", "data", "fingerprint.db"));

    public static string Resolve(string contentRoot, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var p = config["DataPath"];
        if (!string.IsNullOrWhiteSpace(p))
            return Path.GetFullPath(p, contentRoot);
        return PathFor(contentRoot);
    }
    public static void EnsureSchema(string dbPath, ILogger? log = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();
        using var tx = con.BeginTransaction();
        foreach (var ddl in new[]
        {
            @"CREATE TABLE IF NOT EXISTS users(
                id TEXT PRIMARY KEY,
                full_name TEXT NOT NULL,
                external_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL)",
            @"CREATE TABLE IF NOT EXISTS fingerprint_templates(
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL UNIQUE,
                finger_position TEXT NULL,
                template_blob BLOB NOT NULL,
                algorithm TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE)",
            @"CREATE TABLE IF NOT EXISTS audit_events(
                id TEXT PRIMARY KEY,
                action TEXT NOT NULL,
                user_id TEXT NULL,
                success INTEGER NOT NULL,
                score INTEGER NULL,
                error_code TEXT NULL,
                created_at TEXT NOT NULL)",
        })
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = ddl;
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        log?.LogInformation("SQLite schema ensured at {Db}", dbPath);
    }

    public static bool ProbeOk(string dbPath)
    {
        try
        {
            using var con = new SqliteConnection($"Data Source={dbPath}");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
