using System;
using System.IO;
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
