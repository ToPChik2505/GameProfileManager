using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GameProfileManager.Core;
using GameProfileManager.Application;

namespace GameProfileManager.Infrastructure;

// Windows 11 содержит winsqlite3.dll. Один JSON-документ сохраняется в SQLite атомарно.
public sealed class SqliteStateStore : IStateStore
{
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    public string DirectoryPath { get; }
    public SqliteStateStore(string? directoryPath = null) => DirectoryPath = directoryPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameProfileManager");
    public string DatabasePath => Path.Combine(DirectoryPath, "profiles.db");
    public string LogPath => Path.Combine(DirectoryPath, "errors.log");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public AppState Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        using var db = Open();
        Execute(db.Handle, "CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL)");
        IntPtr stmt = Prepare(db.Handle, "SELECT payload FROM state WHERE id=1");
        try
        {
            int result = sqlite3_step(stmt);
            if (result == SqliteDone) return NewState();
            if (result != SqliteRow) throw SqliteError(db.Handle, result, "чтение состояния");
            string? json = Marshal.PtrToStringUTF8(sqlite3_column_text(stmt, 0));
            if (json is null) throw new InvalidDataException($"В базе '{DatabasePath}' сохранено пустое состояние.");
            try
            {
                return JsonSerializer.Deserialize<AppState>(json, JsonOptions) ??
                    throw new InvalidDataException($"В базе '{DatabasePath}' сохранено пустое состояние.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Не удалось разобрать конфигурацию в базе '{DatabasePath}'.", ex);
            }
        }
        finally { sqlite3_finalize(stmt); }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        using var db = Open();
        Execute(db.Handle, "CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL)");
        IntPtr stmt = Prepare(db.Handle, "INSERT INTO state(id,payload) VALUES(1,?1) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, JsonOptions));
            int bindResult = sqlite3_bind_text(stmt, 1, bytes, bytes.Length, new IntPtr(-1));
            if (bindResult != 0) throw SqliteError(db.Handle, bindResult, "подготовка записи состояния");
            int stepResult = sqlite3_step(stmt);
            if (stepResult != SqliteDone) throw SqliteError(db.Handle, stepResult, "запись состояния");
        }
        finally { sqlite3_finalize(stmt); }
    }

    public string ExportBackup(AppState state)
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
        return path;
    }

    public void Log(Exception ex)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:u}] {ex}\n");
    }

    private static AppState NewState()
    {
        var state = new AppState { PresetNamesLocalized = true, PresetProtectionMigrated = true };
        state.Profiles.Add(new() { Name = "Кинематографичный", FpsLimit = 40, Vrr = VrrMode.FixedRefresh, IsBuiltIn = true });
        state.Profiles.Add(new() { Name = "Сбалансированный", FpsLimit = 60, IsBuiltIn = true });
        state.Profiles.Add(new() { Name = "Соревновательный", FpsLimit = 115, IsBuiltIn = true });
        return state;
    }

    private DbHandle Open()
    {
        var bytes = Encoding.UTF8.GetBytes(DatabasePath + "\0");
        int result = sqlite3_open_v2(bytes, out var handle, 0x00000002 | 0x00000004 | 0x00010000, IntPtr.Zero);
        if (result != 0)
        {
            // SQLite может выделить handle даже при ошибке открытия файла.
            // Освобождаем его до исключения, иначе нативная память останется занятой.
            if (handle != IntPtr.Zero) sqlite3_close_v2(handle);
            throw new InvalidOperationException($"SQLite open failed: {result}");
        }
        return new DbHandle(handle);
    }
    private static IntPtr Prepare(IntPtr db, string sql)
    {
        var bytes = Encoding.UTF8.GetBytes(sql + "\0");
        int result = sqlite3_prepare_v2(db, bytes, -1, out var stmt, IntPtr.Zero);
        if (result != 0) throw SqliteError(db, result, "подготовка SQL-команды");
        return stmt;
    }
    private static void Execute(IntPtr db, string sql)
    {
        var stmt = Prepare(db, sql);
        try
        {
            int result = sqlite3_step(stmt);
            if (result != SqliteDone) throw SqliteError(db, result, "выполнение SQL-команды");
        }
        finally { sqlite3_finalize(stmt); }
    }
    private static InvalidOperationException SqliteError(IntPtr db, int result, string operation) =>
        new($"SQLite: ошибка {operation} (код {result}): {Marshal.PtrToStringUTF8(sqlite3_errmsg(db))}");
    private sealed class DbHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Dispose() => sqlite3_close_v2(Handle);
    }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int length, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
}
