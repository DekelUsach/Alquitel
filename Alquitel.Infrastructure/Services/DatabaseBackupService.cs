using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Alquitel.Infrastructure.Services
{
    public class DatabaseBackupService : IDisposable
    {
        private Timer? _timer;
        private readonly TimeSpan _interval = TimeSpan.FromHours(6);

        public void Start()
        {
            AppLog.Information("Starting Database Backup Service...");
            // Do an initial backup after 1 minute, then every 6 hours
            _timer = new Timer(ExecuteBackup, null, TimeSpan.FromMinutes(1), _interval);
        }

        private void ExecuteBackup(object? state)
        {
            try
            {
                var sourceDb = AppPaths.DbFilePath;
                if (!File.Exists(sourceDb))
                {
                    AppLog.Warning("Backup skipped: Source database not found at {DbPath}", sourceDb);
                    return;
                }

                var backupFileName = $"Alquitel_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.db";
                var backupPath = Path.Combine(AppPaths.BackupsFolder, backupFileName);

                // VACUUM INTO uses SQLite's own locking: the copy is always a consistent
                // snapshot even while the app is writing (File.Copy could capture a torn state).
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppPaths.DbConnectionString))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "VACUUM INTO $path";
                    cmd.Parameters.AddWithValue("$path", backupPath);
                    cmd.ExecuteNonQuery();
                }
                AppLog.Information("Database successfully backed up to {BackupPath}", backupPath);
                
                CleanupOldBackups();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Error executing database backup");
            }
        }

        private void CleanupOldBackups()
        {
            try
            {
                var dir = new DirectoryInfo(AppPaths.BackupsFolder);
                var files = dir.GetFiles("Alquitel_Backup_*.db");
                
                if (files.Length > 20)
                {
                    Array.Sort(files, (a, b) => a.CreationTime.CompareTo(b.CreationTime));
                    for (int i = 0; i < files.Length - 20; i++)
                    {
                        files[i].Delete();
                        AppLog.Information("Deleted old backup: {FileName}", files[i].Name);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Error cleaning up old backups");
            }
        }

        // ── Restauración ─────────────────────────────────────────────

        /// <summary>Backup disponible en disco, para el listado de Configuración.</summary>
        public sealed record BackupInfo(string FilePath, DateTime CreatedLocal, long SizeBytes)
        {
            public string DisplayLabel =>
                $"{CreatedLocal:dd/MM/yyyy HH:mm}  ·  {SizeBytes / 1024.0 / 1024.0:0.#} MB";
        }

        /// <summary>Backups existentes, el más reciente primero.</summary>
        public IReadOnlyList<BackupInfo> GetAvailableBackups()
        {
            try
            {
                var dir = new DirectoryInfo(AppPaths.BackupsFolder);
                if (!dir.Exists) return Array.Empty<BackupInfo>();
                return dir.GetFiles("Alquitel_Backup_*.db")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => new BackupInfo(f.FullName, f.LastWriteTime, f.Length))
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "GetAvailableBackups failed");
                return Array.Empty<BackupInfo>();
            }
        }

        /// <summary>
        /// Restaura un backup sobre la base SQLite local. Antes de pisar nada guarda una
        /// copia de seguridad de la base actual (Alquitel_PreRestore_*). La aplicación
        /// debe reiniciarse después para reabrir conexiones sobre el archivo nuevo.
        /// </summary>
        public void RestoreBackup(string backupPath)
        {
            if (!File.Exists(backupPath))
                throw new FileNotFoundException("El backup seleccionado ya no existe.", backupPath);

            var dbPath = AppPaths.DbFilePath;

            // Red de seguridad: la base actual se preserva junto a los backups.
            if (File.Exists(dbPath))
            {
                var safety = Path.Combine(AppPaths.BackupsFolder,
                    $"Alquitel_PreRestore_{DateTime.Now:yyyyMMdd_HHmmss}.db");
                File.Copy(dbPath, safety, overwrite: false);
                AppLog.Information("Pre-restore safety copy created at {Path}", safety);
            }

            // Liberar los handles de SQLite (pooling) antes de tocar el archivo.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            File.Copy(backupPath, dbPath, overwrite: true);

            // WAL/SHM del archivo anterior quedarían inconsistentes con la base restaurada.
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var side = dbPath + suffix;
                try { if (File.Exists(side)) File.Delete(side); }
                catch (Exception ex) { AppLog.Warning(ex, "Could not delete {File}", side); }
            }

            AppLog.Information("Database restored from {Backup}", backupPath);
        }

        public void Stop()
        {
            _timer?.Change(Timeout.Infinite, 0);
            AppLog.Information("Database Backup Service stopped.");
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
