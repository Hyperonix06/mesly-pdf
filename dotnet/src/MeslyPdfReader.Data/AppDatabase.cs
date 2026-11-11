using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MeslyPdfReader.Data;

public sealed record RecentFile(string Path, string Name, DateTime LastOpened, int PageCount, long Size, bool Pinned)
{
    public bool Exists => File.Exists(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed record DocumentState(int Page, double Zoom, string? ZoomMode);

public sealed record TaskRecord(long Id, string Kind, string Input, string? Output, string Status, string? Message, DateTime Started, DateTime? Finished);

/// <summary>
/// Yerel SQLite veritabanı: ayarlar, son açılan dosyalar, belge durumu (son sayfa), görev geçmişi.
/// İş parçacığı güvenlidir (her çağrı kendi bağlantısını açar; SQLite WAL kipinde).
/// </summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;
    public string FilePath { get; }

    public AppDatabase(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = filePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS recent_files (
                path TEXT PRIMARY KEY COLLATE NOCASE, name TEXT NOT NULL, last_opened TEXT NOT NULL,
                page_count INTEGER NOT NULL DEFAULT 0, size INTEGER NOT NULL DEFAULT 0, pinned INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS document_state (
                path TEXT PRIMARY KEY COLLATE NOCASE, page INTEGER NOT NULL, zoom REAL NOT NULL, zoom_mode TEXT, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS task_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, input TEXT NOT NULL, output TEXT,
                status TEXT NOT NULL, message TEXT, started TEXT NOT NULL, finished TEXT);
            CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);
            """);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) Exec(c, "INSERT INTO schema_version(version) VALUES (1)");
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromIso(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime();

    // ── Ayarlar ──
    public string? GetSetting(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string? value)
    {
        using var c = Open();
        if (value is null) Exec(c, "DELETE FROM settings WHERE key=$k", ("$k", key));
        else Exec(c, "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
    }

    public IReadOnlyDictionary<string, string> GetAllSettings()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, string>();
        while (r.Read()) d[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
        return d;
    }

    // ── Son dosyalar ──
    private static string FileNameOf(string path) => path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];

    public void AddRecent(string path, int pageCount, long size)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO recent_files(path,name,last_opened,page_count,size) VALUES($p,$n,$t,$c,$s)
            ON CONFLICT(path) DO UPDATE SET name=excluded.name,last_opened=excluded.last_opened,page_count=excluded.page_count,size=excluded.size
            """, ("$p", path), ("$n", FileNameOf(path)), ("$t", Iso(DateTime.Now)), ("$c", pageCount), ("$s", size));
    }

    public IReadOnlyList<RecentFile> GetRecent(int limit = 30)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path,name,last_opened,page_count,size,pinned FROM recent_files ORDER BY pinned DESC, last_opened DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<RecentFile>();
        while (r.Read()) list.Add(new RecentFile(r.GetString(0), r.GetString(1), FromIso(r.GetString(2)), r.GetInt32(3), r.GetInt64(4), r.GetInt32(5) != 0));
        return list;
    }

    public void SetPinned(string path, bool pinned)
    {
        using var c = Open();
        Exec(c, "UPDATE recent_files SET pinned=$v WHERE path=$p", ("$v", pinned ? 1 : 0), ("$p", path));
    }

    public void RemoveRecent(string path)
    {
        using var c = Open();
        Exec(c, "DELETE FROM recent_files WHERE path=$p", ("$p", path));
    }

    public void ClearRecent()
    {
        using var c = Open();
        Exec(c, "DELETE FROM recent_files WHERE pinned=0");
    }

    // ── Belge durumu (son kalınan sayfa) ──
    public void SaveDocumentState(string path, DocumentState state)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO document_state(path,page,zoom,zoom_mode,updated) VALUES($p,$pg,$z,$m,$t)
            ON CONFLICT(path) DO UPDATE SET page=excluded.page,zoom=excluded.zoom,zoom_mode=excluded.zoom_mode,updated=excluded.updated
            """, ("$p", path), ("$pg", state.Page), ("$z", state.Zoom), ("$m", state.ZoomMode), ("$t", Iso(DateTime.Now)));
    }

    public DocumentState? GetDocumentState(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT page,zoom,zoom_mode FROM document_state WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new DocumentState(r.GetInt32(0), r.GetDouble(1), r.IsDBNull(2) ? null : r.GetString(2)) : null;
    }

    public void ClearDocumentStates()
    {
        using var c = Open();
        Exec(c, "DELETE FROM document_state");
    }

    // ── Görev geçmişi ──
    public long StartTask(string kind, string input)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO task_history(kind,input,status,started) VALUES($k,$i,'running',$t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$i", input); cmd.Parameters.AddWithValue("$t", Iso(DateTime.Now));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void FinishTask(long id, string status, string? output, string? message)
    {
        using var c = Open();
        Exec(c, "UPDATE task_history SET status=$s,output=$o,message=$m,finished=$t WHERE id=$id",
            ("$s", status), ("$o", output), ("$m", message), ("$t", Iso(DateTime.Now)), ("$id", id));
    }

    public IReadOnlyList<TaskRecord> GetTasks(int limit = 100)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,kind,input,output,status,message,started,finished FROM task_history ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<TaskRecord>();
        while (r.Read())
            list.Add(new TaskRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), FromIso(r.GetString(6)), r.IsDBNull(7) ? null : FromIso(r.GetString(7))));
        return list;
    }
}
