using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alquitel.Infrastructure.Security;

namespace Alquitel.Infrastructure.Services;

/// <summary>
/// Persistencia local protegida con DPAPI, reemplazo atómico y cuarentena. El formato
/// legado JSON se acepta una vez y se migra al leerlo correctamente.
/// </summary>
internal sealed class LocalProtectedFileStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ALQDP01\n");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly string _quarantine;

    public LocalProtectedFileStore(string root)
    {
        _root = Path.GetFullPath(root);
        _rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        _quarantine = Path.Combine(_root, "Quarantine");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_quarantine);
    }

    public async Task WriteJsonAsync<T>(
        string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireLeaseAsync(path, cancellationToken);
            var json = JsonSerializer.SerializeToUtf8Bytes(value, options);
            await WriteProtectedCoreAsync(path, json, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public void WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        gate.Wait();
        try
        {
            using var lease = AcquireLease(path);
            var json = JsonSerializer.SerializeToUtf8Bytes(value, options);
            WriteProtectedCore(path, json);
        }
        finally
        {
            gate.Release();
        }
    }

    public void WriteBytes(string path, ReadOnlySpan<byte> plaintext)
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        gate.Wait();
        try
        {
            using var lease = AcquireLease(path);
            WriteProtectedCore(path, plaintext.ToArray());
        }
        finally
        {
            gate.Release();
        }
    }

    public byte[]? ReadBytes(string path, bool migrateLegacy = true)
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        gate.Wait();
        try
        {
            using var lease = AcquireLease(path);
            if (!File.Exists(path)) return null;
            try
            {
                var stored = File.ReadAllBytes(path);
                var legacy = !stored.AsSpan().StartsWith(Magic);
                var plaintext = legacy
                    ? stored
                    : DpapiProtector.Unprotect(stored.AsSpan(Magic.Length).ToArray());
                if (legacy && migrateLegacy)
                    WriteProtectedCore(path, plaintext.ToArray());
                return plaintext;
            }
            catch (Exception ex)
            {
                QuarantineCore(path, "corrupt_or_unreadable");
                AppLog.Warning(
                    "Archivo local {Entry} enviado a cuarentena ({ErrorType})",
                    SafeEntry(path), ex.GetType().Name);
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<T?> ReadJsonAsync<T>(
        string path, JsonSerializerOptions options, CancellationToken cancellationToken = default)
    {
        var stored = await ReadJsonWithFingerprintAsync<T>(path, options, cancellationToken);
        return stored == null ? default : stored.Value;
    }

    public async Task<StoredJson<T>?> ReadJsonWithFingerprintAsync<T>(
        string path, JsonSerializerOptions options, CancellationToken cancellationToken = default)
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireLeaseAsync(path, cancellationToken);
            if (!File.Exists(path)) return default;

            try
            {
                var stored = await File.ReadAllBytesAsync(path, cancellationToken);
                byte[]? json = null;
                try
                {
                    var legacy = !stored.AsSpan().StartsWith(Magic);
                    json = legacy
                        ? stored
                        : DpapiProtector.Unprotect(stored.AsSpan(Magic.Length).ToArray());
                    var value = JsonSerializer.Deserialize<T>(json, options);
                    if (value == null) throw new JsonException("Empty local payload.");

                    if (legacy)
                    {
                        await WriteProtectedCoreAsync(path, json.ToArray(), cancellationToken);
                        stored = await File.ReadAllBytesAsync(path, cancellationToken);
                    }

                    return new StoredJson<T>(value, Fingerprint(stored));
                }
                finally
                {
                    if (json != null) CryptographicOperations.ZeroMemory(json);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                QuarantineCore(path, "corrupt_or_unreadable");
                AppLog.Warning(
                    "Archivo local {Entry} enviado a cuarentena ({ErrorType})",
                    SafeEntry(path), ex.GetType().Name);
                return default;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<StoredJson<TCurrent>?> ReadJsonWithLegacyMigrationAsync<TCurrent, TLegacy>(
        string path,
        JsonSerializerOptions options,
        Func<TCurrent, bool> isCurrentValid,
        Func<TLegacy, bool> isLegacyValid,
        Func<TLegacy, TCurrent> migrate,
        CancellationToken cancellationToken = default)
        where TCurrent : class
        where TLegacy : class
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireLeaseAsync(path, cancellationToken);
            if (!File.Exists(path)) return null;
            try
            {
                var stored = await File.ReadAllBytesAsync(path, cancellationToken);
                byte[]? json = null;
                try
                {
                    var plaintextFormat = !stored.AsSpan().StartsWith(Magic);
                    json = plaintextFormat
                        ? stored
                        : DpapiProtector.Unprotect(stored.AsSpan(Magic.Length).ToArray());

                    TCurrent? current = default;
                    try { current = JsonSerializer.Deserialize<TCurrent>(json, options); }
                    catch (JsonException) { }
                    if (current != null && isCurrentValid(current))
                    {
                        if (plaintextFormat)
                        {
                            await WriteProtectedCoreAsync(path, json.ToArray(), cancellationToken);
                            stored = await File.ReadAllBytesAsync(path, cancellationToken);
                        }
                        return new StoredJson<TCurrent>(current, Fingerprint(stored));
                    }

                    TLegacy? legacy = default;
                    try { legacy = JsonSerializer.Deserialize<TLegacy>(json, options); }
                    catch (JsonException) { }
                    if (legacy == null || !isLegacyValid(legacy))
                        throw new JsonException("Invalid current and legacy local payload.");

                    current = migrate(legacy);
                    var migratedJson = JsonSerializer.SerializeToUtf8Bytes(current, options);
                    await WriteProtectedCoreAsync(path, migratedJson, cancellationToken);
                    stored = await File.ReadAllBytesAsync(path, cancellationToken);
                    return new StoredJson<TCurrent>(current, Fingerprint(stored));
                }
                finally
                {
                    if (json != null) CryptographicOperations.ZeroMemory(json);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                QuarantineCore(path, "corrupt_or_unreadable");
                AppLog.Warning(
                    "Archivo local {Entry} enviado a cuarentena ({ErrorType})",
                    SafeEntry(path), ex.GetType().Name);
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Delete(string path)
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        gate.Wait();
        try
        {
            using var lease = AcquireLease(path);
            if (File.Exists(path)) File.Delete(path);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool DeleteIfUnchanged(string path, string fingerprint)
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        gate.Wait();
        try
        {
            using var lease = AcquireLease(path);
            if (!MatchesFingerprint(path, fingerprint)) return false;
            File.Delete(path);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Quarantine(string path, string reasonCode)
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        gate.Wait();
        try
        {
            using var lease = AcquireLease(path);
            QuarantineCore(path, reasonCode);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool QuarantineIfUnchanged(string path, string fingerprint, string reasonCode)
    {
        path = ValidatePath(path);
        var gate = GateFor(path);
        gate.Wait();
        try
        {
            using var lease = AcquireLease(path);
            if (!MatchesFingerprint(path, fingerprint)) return false;
            QuarantineCore(path, reasonCode);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteProtectedCoreAsync(
        string path, byte[] json, CancellationToken cancellationToken)
    {
        var protectedBytes = DpapiProtector.Protect(json);
        var payload = new byte[Magic.Length + protectedBytes.Length];
        Magic.CopyTo(payload, 0);
        protectedBytes.CopyTo(payload, Magic.Length);
        var temp = TemporaryPathFor(path);
        try
        {
            await using (var stream = new FileStream(
                             temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            Replace(temp, path);
        }
        finally
        {
            TryDeleteTemporary(temp);
            CryptographicOperations.ZeroMemory(json);
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private void WriteProtectedCore(string path, byte[] json)
    {
        var protectedBytes = DpapiProtector.Protect(json);
        var payload = new byte[Magic.Length + protectedBytes.Length];
        Magic.CopyTo(payload, 0);
        protectedBytes.CopyTo(payload, Magic.Length);
        var temp = TemporaryPathFor(path);
        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            Replace(temp, path);
        }
        finally
        {
            TryDeleteTemporary(temp);
            CryptographicOperations.ZeroMemory(json);
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private void QuarantineCore(string path, string reasonCode)
    {
        if (!File.Exists(path)) return;
        Directory.CreateDirectory(_quarantine);
        var bytes = File.ReadAllBytes(path);
        var entry = $"{SafeEntry(path)}_{DateTime.UtcNow:yyyyMMddTHHmmssfff}_{Guid.NewGuid():N}";
        var quarantinedPath = Path.Combine(_quarantine, entry + ".bad");
        if (bytes.AsSpan().StartsWith(Magic))
        {
            File.Move(path, quarantinedPath);
        }
        else
        {
            WriteProtectedCore(quarantinedPath, bytes.ToArray());
            File.Delete(path);
        }

        var metadata = new QuarantineMetadata(
            reasonCode,
            DateTime.UtcNow,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)));
        var metadataPath = Path.Combine(_quarantine, entry + ".metadata.json");
        var metadataJson = JsonSerializer.SerializeToUtf8Bytes(metadata);
        var temp = TemporaryPathFor(metadataPath);
        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(metadataJson);
                stream.Flush(flushToDisk: true);
            }
            Replace(temp, metadataPath);
        }
        finally
        {
            TryDeleteTemporary(temp);
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(metadataJson);
        }
    }

    private string ValidatePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(_quarantine + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La ruta no pertenece al almacenamiento local permitido.");
        return full;
    }

    private static SemaphoreSlim GateFor(string path) =>
        PathLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));

    private static FileStream AcquireLease(string path)
    {
        var lockPath = LockPathFor(path);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.WriteThrough);
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static async Task<FileStream> AcquireLeaseAsync(
        string path, CancellationToken cancellationToken)
    {
        var lockPath = LockPathFor(path);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private static string LockPathFor(string path) => Path.Combine(
        Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.lock");

    private static string TemporaryPathFor(string path) => Path.Combine(
        Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

    private static void Replace(string temp, string destination)
    {
        if (File.Exists(destination))
            File.Move(temp, destination, overwrite: true);
        else
            File.Move(temp, destination);
    }

    private static void TryDeleteTemporary(string temp)
    {
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch
        {
            // El archivo temporal nunca se enumera como dato válido y se limpia en el próximo inicio.
        }
    }

    private static string SafeEntry(string path)
    {
        var nameBytes = Encoding.UTF8.GetBytes(Path.GetFileName(path));
        try { return "entry_" + Convert.ToHexString(SHA256.HashData(nameBytes))[..16]; }
        finally { CryptographicOperations.ZeroMemory(nameBytes); }
    }

    private static bool MatchesFingerprint(string path, string expected) =>
        File.Exists(path) && string.Equals(
            Fingerprint(File.ReadAllBytes(path)), expected, StringComparison.Ordinal);

    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed record QuarantineMetadata(
        string ReasonCode,
        DateTime QuarantinedUtc,
        long SizeBytes,
        string Sha256);

    internal sealed record StoredJson<T>(T Value, string Fingerprint);
}
