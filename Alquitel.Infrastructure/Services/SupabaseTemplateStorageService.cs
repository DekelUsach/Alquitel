using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Alquitel.Core.Interfaces;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación de <see cref="ITemplateStorageService"/> contra Supabase Storage
    /// (bucket privado "templates", API REST /storage/v1).
    ///
    /// Modelo de credenciales (dos llaves):
    ///  - <b>anon key</b>: viaja en cada binario. Solo LECTURA del bucket (policy
    ///    templates_read_anon). Sirve para que cada puesto descargue la plantilla vigente.
    ///  - <b>service key</b>: solo presente en la máquina del Admin (variable de entorno
    ///    ALQUITEL_Database__Supabase__ServiceKey o appsettings.local.json). Requerida para
    ///    PUBLICAR/actualizar plantillas. No se commitea ni se distribuye en el binario.
    ///
    /// Así un atacante que extrae la anon key del ejecutable no puede sobrescribir las
    /// plantillas .docx (vector de RCE vía macros): solo puede leerlas.
    /// Cada descarga exitosa se cachea en %LocalAppData%\Alquitel\templates_cache para
    /// que la generación de documentos funcione sin internet con la última versión.
    /// </summary>
    public class SupabaseTemplateStorageService : ITemplateStorageService
    {
        private const string Bucket = "templates";
        private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private readonly string _url;
        private readonly string _anonKey;
        private readonly string _serviceKey;

        public SupabaseTemplateStorageService(string? url, string? anonKey, string? serviceKey)
        {
            _url = (url ?? string.Empty).TrimEnd('/');
            _anonKey = anonKey ?? string.Empty;
            _serviceKey = serviceKey ?? string.Empty;
        }

        /// <summary>True cuando hay Url + anon key: permite descargar/consultar plantillas.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(_url) && !string.IsNullOrWhiteSpace(_anonKey);

        /// <summary>True solo en la máquina que tiene la service key: permite publicar.</summary>
        public bool CanPublish => !string.IsNullOrWhiteSpace(_url) && !string.IsNullOrWhiteSpace(_serviceKey);

        private static string ObjectName(TemplateKind kind) => kind switch
        {
            TemplateKind.Presupuesto => "presupuesto.docx",
            TemplateKind.OF => "of.docx",
            TemplateKind.OT => "ot.docx",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        private static string CachePath(TemplateKind kind) =>
            Path.Combine(AppPaths.TemplatesCacheFolder, ObjectName(kind));

        private HttpRequestMessage NewRequest(HttpMethod method, string relativePath, string key)
        {
            var req = new HttpRequestMessage(method, $"{_url}/storage/v1/{relativePath}");
            req.Headers.Add("apikey", key);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return req;
        }

        public async Task PublishTemplateAsync(TemplateKind kind, string localFilePath)
        {
            if (!CanPublish)
                throw new InvalidOperationException(
                    "Esta máquina no puede publicar plantillas: falta la service key de Supabase. " +
                    "Configurá la variable de entorno ALQUITEL_Database__Supabase__ServiceKey " +
                    "(o Database:Supabase:ServiceKey en appsettings.local.json) solo en el equipo del Admin. " +
                    "La anon key incluida en la app es de solo lectura.");
            if (!File.Exists(localFilePath))
                throw new FileNotFoundException("No se encontró el archivo de plantilla a publicar.", localFilePath);

            byte[] bytes = await File.ReadAllBytesAsync(localFilePath);

            using var req = NewRequest(HttpMethod.Post, $"object/{Bucket}/{ObjectName(kind)}", _serviceKey);
            req.Headers.Add("x-upsert", "true");
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Supabase Storage rechazó la publicación ({(int)resp.StatusCode}): {body}");
            }

            // La versión recién publicada ya es la vigente: actualizar cache local.
            try
            {
                Directory.CreateDirectory(AppPaths.TemplatesCacheFolder);
                await File.WriteAllBytesAsync(CachePath(kind), bytes);
                // El ETag guardado quedó viejo: se borra para que el próximo Resolve
                // valide contra el servidor y traiga el ETag de la versión nueva.
                var etagPath = CachePath(kind) + ".etag";
                if (File.Exists(etagPath)) File.Delete(etagPath);
            }
            catch (Exception ex) { AppLog.Warning(ex, "No se pudo actualizar el cache local de plantillas tras publicar"); }

            AppLog.Information("Plantilla {Kind} publicada en Supabase Storage ({Bytes} bytes)", kind, bytes.Length);
        }

        public async Task<string?> ResolveTemplateAsync(TemplateKind kind)
        {
            string cache = CachePath(kind);
            string etagPath = cache + ".etag";
            if (!IsConfigured)
                return File.Exists(cache) ? cache : null;

            try
            {
                using var req = NewRequest(HttpMethod.Get, $"object/{Bucket}/{ObjectName(kind)}", _anonKey);

                // Cache condicional: si tenemos la versión vigente, el servidor responde
                // 304 sin cuerpo en lugar de mandar el .docx completo en cada generación.
                if (File.Exists(cache) && File.Exists(etagPath))
                {
                    var cachedEtag = (await File.ReadAllTextAsync(etagPath)).Trim();
                    if (cachedEtag.Length > 0)
                        req.Headers.TryAddWithoutValidation("If-None-Match", cachedEtag);
                }

                using var resp = await _http.SendAsync(req);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                    return cache;

                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return File.Exists(cache) ? cache : null;

                resp.EnsureSuccessStatusCode();
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync();

                Directory.CreateDirectory(AppPaths.TemplatesCacheFolder);
                // Escritura atómica: si la app se cierra a mitad de descarga no queda un docx corrupto.
                string tmp = cache + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, cache, overwrite: true);

                try
                {
                    var etag = resp.Headers.ETag?.ToString();
                    if (!string.IsNullOrEmpty(etag))
                        await File.WriteAllTextAsync(etagPath, etag);
                    else if (File.Exists(etagPath))
                        File.Delete(etagPath);
                }
                catch (Exception ex) { AppLog.Warning(ex, "No se pudo guardar el ETag de la plantilla {Kind}", kind); }

                return cache;
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "No se pudo descargar la plantilla {Kind} del servidor; se usa el cache local si existe", kind);
                return File.Exists(cache) ? cache : null;
            }
        }

        public async Task<TemplateCloudStatus> GetStatusAsync(TemplateKind kind)
        {
            if (!IsConfigured)
                return new TemplateCloudStatus(kind, false, null, null);

            try
            {
                using var req = NewRequest(HttpMethod.Post, $"object/list/{Bucket}", _anonKey);
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { prefix = "", limit = 100 }),
                    Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req);
                resp.EnsureSuccessStatusCode();

                using var docJson = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                string target = ObjectName(kind);
                foreach (var obj in docJson.RootElement.EnumerateArray())
                {
                    if (!string.Equals(obj.GetProperty("name").GetString(), target, StringComparison.OrdinalIgnoreCase))
                        continue;

                    DateTime? updated = null;
                    if (obj.TryGetProperty("updated_at", out var up) && up.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(up.GetString(), out var parsed))
                        updated = parsed.ToLocalTime();

                    long? size = null;
                    if (obj.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                        meta.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var szVal))
                        size = szVal;

                    return new TemplateCloudStatus(kind, true, updated, size);
                }
                return new TemplateCloudStatus(kind, false, null, null);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "GetStatusAsync({Kind}) failed", kind);
                return new TemplateCloudStatus(kind, false, null, null);
            }
        }
    }
}
