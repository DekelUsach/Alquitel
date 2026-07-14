using System.Text.Json;

namespace Alquitel.Mobile.Services;

/// <summary>
/// Configuración de la app mobile. Fuentes, en orden de prioridad:
/// 1. SecureStorage (editable desde Ajustes en el dispositivo).
/// 2. Asset embebido Resources/Raw/appsettings.mobile.json (gitignoreado; ver
///    appsettings.mobile.example.json). Se embebe al compilar el APK de distribución.
/// La ConnectionString nunca se persiste fuera de SecureStorage.
/// </summary>
public static class AppConfig
{
    public const string DefaultSupabaseUrl = "https://qgtaugmxmoxtpxvmugvt.supabase.co";

    public static string ConnectionString { get; private set; } = string.Empty;
    public static string SupabaseUrl { get; private set; } = DefaultSupabaseUrl;
    public static string? PollinationsApiKey { get; private set; }

    // Parámetros de Smart Search (mismos defaults que el desktop).
    public static List<string> SmartSearchStopWords { get; } = new()
    {
        "de", "para", "con", "el", "la", "los", "las", "un", "una", "y", "o",
        "plus", "pro", "edition", "business", "servicio"
    };
    public static double SmartSearchThreshold => 4.0;
    public static double SmartSearchMargin => 0.35;

    public static bool IsDbConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    private static bool _initialized;

    public static async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        // 1) Asset embebido (valores por defecto del build de distribución).
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("appsettings.mobile.json");
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("ConnectionString", out var cs) && cs.ValueKind == JsonValueKind.String)
                ConnectionString = cs.GetString() ?? string.Empty;
            if (root.TryGetProperty("SupabaseUrl", out var url) && url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString()))
                SupabaseUrl = url.GetString()!.TrimEnd('/');
            if (root.TryGetProperty("PollinationsApiKey", out var key) && key.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString()))
                PollinationsApiKey = key.GetString();
        }
        catch (FileNotFoundException)
        {
            // Sin asset embebido: la config viene de SecureStorage / Ajustes.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AppConfig asset load failed: {ex.Message}");
        }

        // 2) Overrides guardados en el dispositivo.
        var storedCs = await TryGetSecureAsync("db_connection");
        if (!string.IsNullOrWhiteSpace(storedCs)) ConnectionString = storedCs;

        var storedAi = await TryGetSecureAsync("pollinations_key");
        if (!string.IsNullOrWhiteSpace(storedAi)) PollinationsApiKey = storedAi;
    }

    public static async Task SaveConnectionStringAsync(string value)
    {
        ConnectionString = value.Trim();
        await SecureStorage.Default.SetAsync("db_connection", ConnectionString);
    }

    public static async Task SavePollinationsKeyAsync(string? value)
    {
        PollinationsApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (PollinationsApiKey == null)
            SecureStorage.Default.Remove("pollinations_key");
        else
            await SecureStorage.Default.SetAsync("pollinations_key", PollinationsApiKey);
    }

    private static async Task<string?> TryGetSecureAsync(string key)
    {
        try { return await SecureStorage.Default.GetAsync(key); }
        catch { return null; }
    }
}
