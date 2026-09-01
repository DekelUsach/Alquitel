using Microsoft.Data.Sqlite;

namespace Alquitel.Infrastructure.Services;

public sealed class DatabaseBackupService : IDisposable
{
    private readonly TimeSpan _interval = TimeSpan.FromHours(6);
    private readonly string _dbPath;
    private readonly string _backupsFolder;
    private readonly int _retention;
    private readonly LocalProtectedFileStore _store;
    private readonly string _pendingRestorePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;

    public DatabaseBackupService()
        : this(AppPaths.DbFilePath, AppPaths.BackupsFolder, 20)
    {
    }

    public DatabaseBackupService(string dbPath, string backupsFolder, int retention = 20)
    {
        if (retention < 1) throw new ArgumentOutOfRangeException(nameof(retention));
        _dbPath = Path.GetFullPath(dbPath);
        _backupsFolder = Path.GetFullPath(backupsFolder);
        _retention = retention;
        Directory.CreateDirectory(_backupsFolder);
        _store = new LocalProtectedFileStore(_backupsFolder);
        _pendingRestorePath = Path.Combine(_backupsFolder, "pending_restore.alq");
        CleanupStagingArtifacts();
    }

    public void Start()
    {
        if (_timer != null) return;
        AppLog.Information("Starting Database Backup Service...");
        _timer = new Timer(ExecuteBackup, null, TimeSpan.FromMinutes(1), _interval);
    }

    private void ExecuteBackup(object? state)
    {
        try { CreateBackupNow(); }
        catch (Exception ex) { AppLog.Error("Error executing database backup ({ErrorType})", ex.GetType().Name); }
    }

    public BackupInfo? CreateBackupNow()
    {
        if (!_gate.Wait(0)) return null;
        string? snapshot = null;
        try
        {
            if (!File.Exists(_dbPath))
            {
                AppLog.Warning("Backup skipped: local database not found");
                return null;
            }

            var backupName = $"Alquitel_Backup_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.db";
            var backupPath = Path.Combine(_backupsFolder, backupName);
            snapshot = SnapshotPath();
            CreateSqliteSnapshot(snapshot);
            ValidateSqlite(snapshot);
            var plaintext = File.ReadAllBytes(snapshot);
            try { _store.WriteBytes(backupPath, plaintext); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }

            var info = new FileInfo(backupPath);
            AppLog.Information("Database backup created: {BackupName}", info.Name);
            CleanupOldBackups();
            return new BackupInfo(info.FullName, info.LastWriteTime, info.Length);
        }
        finally
        {
            TryDelete(snapshot);
            _gate.Release();
        }
    }

    private void CleanupOldBackups()
    {
        try
        {
            var files = new DirectoryInfo(_backupsFolder)
                .GetFiles("Alquitel_Backup_*.db")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .ToList();

            foreach (var obsolete in files.Skip(_retention))
            {
                _store.Delete(obsolete.FullName);
                AppLog.Information("Deleted old backup: {FileName}", obsolete.Name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Error cleaning up old backups ({ErrorType})", ex.GetType().Name);
        }
    }

    public sealed record BackupInfo(string FilePath, DateTime CreatedLocal, long SizeBytes)
    {
        public string DisplayLabel =>
            $"{CreatedLocal:dd/MM/yyyy HH:mm}  ·  {SizeBytes / 1024.0 / 1024.0:0.#} MB";
    }

    public sealed class ApplicationDatabaseLease : IDisposable
    {
        private readonly string _dbPath;
        private FileStream? _stream;

        internal ApplicationDatabaseLease(string dbPath, FileStream stream)
        {
            _dbPath = dbPath;
            _stream = stream;
        }

        internal bool IsActiveFor(string dbPath) =>
            _stream != null &&
            string.Equals(_dbPath, dbPath, StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
        }
    }

    public IReadOnlyList<BackupInfo> GetAvailableBackups()
    {
        try
        {
            var dir = new DirectoryInfo(_backupsFolder);
            if (!dir.Exists) return Array.Empty<BackupInfo>();
            return dir.GetFiles("Alquitel_*.db")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new BackupInfo(f.FullName, f.LastWriteTime, f.Length))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warning("GetAvailableBackups failed ({ErrorType})", ex.GetType().Name);
            return Array.Empty<BackupInfo>();
        }
    }

    public void RestoreBackup(string backupPath)
    {
        if (!_gate.Wait(0))
            throw new InvalidOperationException("Ya hay una operación de backup en curso.");

        string? validationTemp = null;
        try
        {
            using var restoreLease = AcquireRestoreLease();
            backupPath = ValidateBackupPath(backupPath);
            var plaintext = _store.ReadBytes(backupPath);
            if (plaintext == null)
                throw new InvalidDataException("El backup está corrupto o no puede descifrarse.");

            try
            {
                validationTemp = RestoreTempPath();
                WriteThrough(validationTemp, plaintext);
                ValidateSqlite(validationTemp);
                _store.WriteBytes(_pendingRestorePath, plaintext);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
            AppLog.Information("Database restore staged from {BackupName}", Path.GetFileName(backupPath));
        }
        catch (InvalidDataException)
        {
            if (backupPath != null && File.Exists(backupPath))
                _store.Quarantine(backupPath, "invalid_sqlite_backup");
            throw;
        }
        finally
        {
            TryDelete(validationTemp);
            _gate.Release();
        }
    }

    /// <summary>
    /// Aplica el restore pendiente antes de inicializar EF, cuando todavía no existen
    /// contextos ni escritores activos. Si falla, el pedido protegido queda para reintento.
    /// </summary>
    public ApplicationDatabaseLease AcquireApplicationDatabaseLease()
    {
        var path = Path.Combine(Path.GetDirectoryName(_dbPath)!, ".alquitel.database.lock");
        try
        {
            return new ApplicationDatabaseLease(
                _dbPath,
                new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.WriteThrough));
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Alquitel ya está abierto en otra instancia para esta base de datos.", ex);
        }
    }

    public bool ApplyPendingRestoreAtStartup(ApplicationDatabaseLease applicationLease)
    {
        ArgumentNullException.ThrowIfNull(applicationLease);
        if (!applicationLease.IsActiveFor(_dbPath))
            throw new InvalidOperationException("El restore requiere el bloqueo exclusivo de la aplicación.");
        if (!File.Exists(_pendingRestorePath)) return false;
        _gate.Wait();
        string? restoreTemp = null;
        try
        {
            using var restoreLease = AcquireRestoreLease();
            var plaintext = _store.ReadBytes(_pendingRestorePath, migrateLegacy: false);
            if (plaintext == null)
                throw new InvalidDataException("El restore pendiente no puede descifrarse.");
            restoreTemp = RestoreTempPath();
            try { WriteThrough(restoreTemp, plaintext); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
            ValidateSqlite(restoreTemp);

            if (File.Exists(_dbPath))
            {
                var safetyName = $"Alquitel_PreRestore_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.db";
                var safetyPath = Path.Combine(_backupsFolder, safetyName);
                var snapshot = SnapshotPath();
                try
                {
                    CreateSqliteSnapshot(snapshot);
                    var currentBytes = File.ReadAllBytes(snapshot);
                    try { _store.WriteBytes(safetyPath, currentBytes); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(currentBytes); }
                }
                finally { TryDelete(snapshot); }

                CheckpointCurrentDatabase();
            }

            SqliteConnection.ClearAllPools();
            TryDeleteRequired(_dbPath + "-wal");
            TryDeleteRequired(_dbPath + "-shm");
            File.Move(restoreTemp, _dbPath, overwrite: true);
            restoreTemp = null;
            _store.Delete(_pendingRestorePath);
            AppLog.Information("Pending database restore applied at startup");
            CleanupOldSafetySnapshots();
            return true;
        }
        catch (InvalidDataException)
        {
            if (File.Exists(_pendingRestorePath))
                _store.Quarantine(_pendingRestorePath, "invalid_pending_restore");
            throw;
        }
        finally
        {
            TryDelete(restoreTemp);
            _gate.Release();
        }
    }

    private string ValidateBackupPath(string path)
    {
        var full = Path.GetFullPath(path);
        var prefix = _backupsFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("Alquitel_", StringComparison.Ordinal) ||
            !full.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El archivo no pertenece al directorio de backups.");
        if (!File.Exists(full)) throw new FileNotFoundException("El backup seleccionado ya no existe.", full);
        return full;
    }

    private void CreateSqliteSnapshot(string destination)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path";
        command.Parameters.AddWithValue("$path", destination);
        command.ExecuteNonQuery();
        File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.Hidden);
    }

    private static void ValidateSqlite(string path)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            var result = command.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La verificación de integridad del backup falló.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            throw new InvalidDataException("El archivo no es un backup SQLite válido.", ex);
        }
    }

    private string SnapshotPath() => Path.Combine(
        _backupsFolder, $".snapshot_{Guid.NewGuid():N}.tmp");

    private string RestoreTempPath() => Path.Combine(
        Path.GetDirectoryName(_dbPath)!,
        $".{Path.GetFileName(_dbPath)}.{Guid.NewGuid():N}.restore.tmp");

    private FileStream AcquireRestoreLease() => new(
        Path.Combine(_backupsFolder, ".restore-operation.lock"),
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None,
        1,
        FileOptions.WriteThrough);

    private static void WriteThrough(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
    }

    private void CheckpointCurrentDatabase()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 0)
            throw new IOException("No se pudo cerrar el WAL antes del restore.");
    }

    private void CleanupStagingArtifacts()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_backupsFolder, ".snapshot_*.tmp"))
                TryDelete(file);
            var dbFolder = Path.GetDirectoryName(_dbPath);
            if (dbFolder != null && Directory.Exists(dbFolder))
                foreach (var file in Directory.EnumerateFiles(
                             dbFolder, $".{Path.GetFileName(_dbPath)}.*.restore.tmp"))
                    TryDelete(file);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not clean stale backup staging ({ErrorType})", ex.GetType().Name);
        }
    }

    private void CleanupOldSafetySnapshots()
    {
        try
        {
            var keep = Math.Min(_retention, 5);
            var snapshots = new DirectoryInfo(_backupsFolder)
                .GetFiles("Alquitel_PreRestore_*.db")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var obsolete in snapshots.Skip(keep))
                _store.Delete(obsolete.FullName);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Could not prune restore snapshots ({ErrorType})", ex.GetType().Name);
        }
    }

    private static void TryDeleteRequired(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDelete(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Warning("Could not clean a temporary backup file ({ErrorType})", ex.GetType().Name); }
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, 0);
        AppLog.Information("Database Backup Service stopped.");
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
