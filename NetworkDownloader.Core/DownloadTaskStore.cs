using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NetworkDownloader.Core;

public interface ITaskDataProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsTaskDataProtector : ITaskDataProtector
{
    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}

// One app instance owns this catalog. Engine sidecars still own recoverable byte offsets.
public sealed class DownloadTaskStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly FileStream lease;
    private readonly ITaskDataProtector protector;
    private readonly object sync = new();

    public DownloadTaskStore(string path, ITaskDataProtector protector)
    {
        this.protector = protector;
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lease = new FileStream(path + ".instance.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema > 1) throw new InvalidDataException("任务数据库版本较新，请使用新版下载器。");
            Execute("""
                CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY, version INTEGER NOT NULL, payload BLOB NOT NULL);
                CREATE TABLE IF NOT EXISTS task_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id TEXT NOT NULL, state TEXT NOT NULL, occurred_at TEXT NOT NULL);
                PRAGMA user_version=1;
                """);
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("任务数据库校验失败，未覆盖原有数据。请从备份恢复。");
        }
        catch { connection.Dispose(); lease.Dispose(); throw; }
    }

    public void Backup()
    {
        lock (sync)
        {
            // Call only after all payloads have been decrypted and validated successfully.
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = connection.DataSource + ".bak", Pooling = false }.ToString());
            backup.Open();
            connection.BackupDatabase(backup);
        }
    }

    public IReadOnlyList<DownloadTaskRecord> Load()
    {
        lock (sync)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,version,payload FROM tasks";
            using var reader = command.ExecuteReader();
            var records = new List<DownloadTaskRecord>();
            while (reader.Read())
            {
                var bytes = protector.Unprotect((byte[])reader[2]);
                var record = JsonSerializer.Deserialize<DownloadTaskRecord>(bytes)
                    ?? throw new InvalidDataException("任务记录为空，未丢弃原有数据库。");
                if (record.Id.ToString("N") != reader.GetString(0) || record.Version != reader.GetInt64(1) ||
                    !Enum.IsDefined(record.State) || record.Connections is < 1 or > 16 ||
                    !Path.IsPathFullyQualified(record.SaveDirectory)) throw new InvalidDataException("任务记录格式无效。");
                records.Add(record);
            }
            return records.OrderBy(t => t.CreatedAt).ToArray();
        }
    }

    public DownloadTaskRecord Save(DownloadTaskRecord record, bool transition)
    {
        lock (sync)
        {
            var next = record with { Version = record.Version + 1, UpdatedAt = DateTimeOffset.UtcNow };
            var payload = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(next));
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = record.Version == 0
                ? "INSERT INTO tasks(id,version,payload) VALUES($id,$next,$payload)"
                : "UPDATE tasks SET version=$next,payload=$payload WHERE id=$id AND version=$version";
            command.Parameters.AddWithValue("$id", record.Id.ToString("N"));
            command.Parameters.AddWithValue("$next", next.Version);
            command.Parameters.AddWithValue("$version", record.Version);
            command.Parameters.AddWithValue("$payload", payload);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("任务记录已变化，请重新加载。");
            if (transition)
            {
                using var audit = connection.CreateCommand();
                audit.Transaction = transaction;
                audit.CommandText = "INSERT INTO task_events(task_id,state,occurred_at) VALUES($id,$state,$time)";
                audit.Parameters.AddWithValue("$id", record.Id.ToString("N"));
                audit.Parameters.AddWithValue("$state", record.State.ToString());
                audit.Parameters.AddWithValue("$time", next.UpdatedAt.ToString("O"));
                audit.ExecuteNonQuery();
            }
            transaction.Commit();
            return next;
        }
    }

    public void Remove(DownloadTaskRecord record)
    {
        lock (sync)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM tasks WHERE id=$id AND version=$version";
            command.Parameters.AddWithValue("$id", record.Id.ToString("N"));
            command.Parameters.AddWithValue("$version", record.Version);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("任务记录已变化，未移除。");
            command.CommandText = "DELETE FROM task_events WHERE task_id=$id";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private void Execute(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose() { lock (sync) { connection.Dispose(); lease.Dispose(); } }
}
