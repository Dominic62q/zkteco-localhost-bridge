using Microsoft.Data.Sqlite;

namespace Bridge.Data;

/// <summary>SQLite CRUD for users, encrypted templates, audit events. No biometrics in logs.</summary>
public sealed class Store
{
    private readonly string _dbPath;
    private readonly ILogger _log;

    public Store(string dbPath, ILogger log)
    {
        _dbPath = dbPath;
        _log = log;
    }

    public sealed record StoredTemplate(string UserId, byte[] CipherBlob, string Algorithm);

    public void SaveEnrolment(string userId, string fullName, string? externalId,
        byte[] cipherBlob, string algorithm)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var cmd = con.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO users(id, full_name, external_id, created_at, updated_at) VALUES($id, $n, $e, $t, $t)";
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.Parameters.AddWithValue("$n", fullName);
            cmd.Parameters.AddWithValue("$e", (object?)externalId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = con.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO fingerprint_templates(id, user_id, finger_position, template_blob, algorithm, created_at) VALUES($id, $u, NULL, $b, $a, $t)";
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$u", userId);
            cmd.Parameters.AddWithValue("$b", cipherBlob);
            cmd.Parameters.AddWithValue("$a", algorithm);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<StoredTemplate> LoadAllTemplates()
    {
        var out_ = new List<StoredTemplate>();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT user_id, template_blob, algorithm FROM fingerprint_templates";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            out_.Add(new StoredTemplate(
                r.GetString(0),
                (byte[])r.GetValue(1),
                r.GetString(2)));
        }
        return out_;
    }

    public void Audit(string action, string? userId, bool success, int? score, string? errorCode)
    {
        try
        {
            using var con = Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "INSERT INTO audit_events(id, action, user_id, success, score, error_code, created_at) VALUES($id, $a, $u, $s, $c, $e, $t)";
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$a", action);
            cmd.Parameters.AddWithValue("$u", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", success ? 1 : 0);
            cmd.Parameters.AddWithValue("$c", (object?)score ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)errorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Audit write failed for {Action}", action);
        }
    }

    public sealed record UserRecord(string Id, string FullName, string? ExternalId, string CreatedAt);

    public List<UserRecord> ListUsers()
    {
        var out_ = new List<UserRecord>();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id, full_name, external_id, created_at FROM users ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            out_.Add(new UserRecord(r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)));
        }
        return out_;
    }

    public UserRecord? GetUser(string id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id, full_name, external_id, created_at FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new UserRecord(r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3))
            : null;
    }

    public StoredTemplate? LoadTemplate(string userId)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT user_id, template_blob, algorithm FROM fingerprint_templates WHERE user_id = $u";
        cmd.Parameters.AddWithValue("$u", userId);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new StoredTemplate(r.GetString(0), (byte[])r.GetValue(1), r.GetString(2))
            : null;
    }

    /// <summary>Delete user and template together. Returns false when unknown.</summary>
    public bool DeleteUser(string id)
    {
        using var con = Open();
        using var tx = con.BeginTransaction();
        using (var cmd = con.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM fingerprint_templates WHERE user_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        int rows;
        using (var cmd = con.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM users WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            rows = cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return rows > 0;
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        return con;
    }
}
