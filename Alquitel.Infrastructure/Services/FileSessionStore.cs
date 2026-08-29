using System;
using System.IO;
using System.Security.Cryptography;
using Alquitel.Core.Security;
using Alquitel.Infrastructure.Security;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación de <see cref="Core.Interfaces.ISessionStore"/> sobre un archivo en
    /// <see cref="AppPaths.SessionFilePath"/>. Cualquier error de I/O, de firma o de
    /// vigencia se trata como "sin sesión guardada" — nunca debe tumbar el startup.
    ///
    /// Antes el archivo era un JSON plano <c>{"UserId": "..."}</c>: cambiarlo a mano por
    /// el Guid de un Admin salteaba el login con contraseña (escalada de privilegios
    /// local). Ahora se guarda un <see cref="SessionToken"/> firmado con HMAC-SHA256; la
    /// clave vive en un archivo aparte protegido con DPAPI del usuario actual.
    ///
    /// Alcance real de la defensa: bloquea la edición manual del archivo, la copia de la
    /// sesión a otra máquina o cuenta de Windows, y la reutilización tras un cambio de
    /// contraseña. NO pretende resistir a código arbitrario corriendo como ese mismo
    /// usuario de Windows (ahí ya está todo perdido, incluida la base local).
    /// </summary>
    public class FileSessionStore : Core.Interfaces.ISessionStore
    {
        private static string KeyFilePath => AppPaths.SessionFilePath + ".key";

        private readonly object _keyLock = new();
        private byte[]? _cachedKey;

        public void Save(Guid userId, string passwordFingerprint)
        {
            try
            {
                var key = GetOrCreateKey();
                if (key == null) return;

                var token = SessionToken.Issue(userId, passwordFingerprint, DateTimeOffset.UtcNow, key);
                File.WriteAllText(AppPaths.SessionFilePath, token);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "No se pudo guardar la sesión persistente");
            }
        }

        public bool TryLoad(TimeSpan maxAge, out Guid userId, out string passwordFingerprint, out DateTimeOffset savedAtUtc)
        {
            userId = Guid.Empty;
            passwordFingerprint = string.Empty;
            savedAtUtc = default;

            try
            {
                if (!File.Exists(AppPaths.SessionFilePath)) return false;

                var key = LoadKey();
                if (key == null)
                {
                    // Sin clave no hay forma de validar la firma: la sesión guardada es
                    // inservible y se descarta para no dejar basura en disco.
                    Clear();
                    return false;
                }

                var token = File.ReadAllText(AppPaths.SessionFilePath).Trim();
                if (!SessionToken.TryValidate(token, key, DateTimeOffset.UtcNow, maxAge,
                        out userId, out passwordFingerprint, out savedAtUtc))
                {
                    AppLog.Information("Sesión guardada inválida o vencida: se pide login manual");
                    userId = Guid.Empty;
                    passwordFingerprint = string.Empty;
                    savedAtUtc = default;
                    return false;
                }

                return userId != Guid.Empty;
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Sesión persistente corrupta o ilegible, se ignora");
                userId = Guid.Empty;
                passwordFingerprint = string.Empty;
                savedAtUtc = default;
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

        // ── Clave de firma ───────────────────────────────────────────

        private byte[]? GetOrCreateKey()
        {
            lock (_keyLock)
            {
                var existing = LoadKeyUnlocked();
                if (existing != null) return existing;

                try
                {
                    var key = RandomNumberGenerator.GetBytes(SessionToken.KeySize);
                    File.WriteAllBytes(KeyFilePath, DpapiProtector.Protect(key));
                    _cachedKey = key;
                    return key;
                }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "No se pudo crear la clave de sesión (DPAPI); no habrá sesión persistente");
                    return null;
                }
            }
        }

        private byte[]? LoadKey()
        {
            lock (_keyLock) return LoadKeyUnlocked();
        }

        private byte[]? LoadKeyUnlocked()
        {
            if (_cachedKey != null) return _cachedKey;

            try
            {
                if (!File.Exists(KeyFilePath)) return null;
                var key = DpapiProtector.Unprotect(File.ReadAllBytes(KeyFilePath));
                if (key.Length < 16) return null;
                _cachedKey = key;
                return key;
            }
            catch (Exception ex)
            {
                // Perfil de Windows distinto, archivo corrupto o copiado de otra máquina:
                // se descarta la clave y con ella la sesión que firmaba.
                AppLog.Warning(ex, "No se pudo leer la clave de sesión; se descarta la sesión guardada");
                try { if (File.Exists(KeyFilePath)) File.Delete(KeyFilePath); } catch { /* best-effort */ }
                return null;
            }
        }
    }
}
