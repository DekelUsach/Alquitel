using System;
using System.IO;
using System.Text.Json;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación de <see cref="Core.Interfaces.ISessionStore"/> sobre un archivo
    /// JSON en <see cref="AppPaths.SessionFilePath"/>. Cualquier error de I/O o de
    /// parseo se trata como "sin sesión guardada" — nunca debe tumbar el startup.
    /// </summary>
    public class FileSessionStore : Core.Interfaces.ISessionStore
    {
        private class SessionData
        {
            public Guid UserId { get; set; }
            public DateTimeOffset SavedAtUtc { get; set; }
        }

        public void Save(Guid userId)
        {
            try
            {
                var data = new SessionData { UserId = userId, SavedAtUtc = DateTimeOffset.UtcNow };
                File.WriteAllText(AppPaths.SessionFilePath, JsonSerializer.Serialize(data));
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "No se pudo guardar la sesión persistente");
            }
        }

        public bool TryLoad(out Guid userId, out DateTimeOffset savedAtUtc)
        {
            userId = Guid.Empty;
            savedAtUtc = default;

            try
            {
                if (!File.Exists(AppPaths.SessionFilePath)) return false;

                var json = File.ReadAllText(AppPaths.SessionFilePath);
                var data = JsonSerializer.Deserialize<SessionData>(json);
                if (data == null || data.UserId == Guid.Empty) return false;

                userId = data.UserId;
                savedAtUtc = data.SavedAtUtc;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Sesión persistente corrupta o ilegible, se ignora");
                return false;
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(AppPaths.SessionFilePath))
                    File.Delete(AppPaths.SessionFilePath);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "No se pudo borrar la sesión persistente");
            }
        }
    }
}
