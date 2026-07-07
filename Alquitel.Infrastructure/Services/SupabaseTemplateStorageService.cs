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
    /// (bucket privado "templates", API REST /storage/v1). Usa la anon key del proyecto;
    /// el bucket tiene policies propias y el resto del storage queda inaccesible.
    /// Cada descarga exitosa se cachea en %LocalAppData%\Alquitel\templates_cache para
    /// que la generación de documentos funcione sin internet con la última versión.
    /// </summary>
    public class SupabaseTemplateStorageService : ITemplateStorageService
    {
        private const string Bucket = "templates";
        private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private readonly string _url;
        private readonly string _apiKey;

        public SupabaseTemplateStorageService(string? url, string? apiKey)
        {
            _url = (url ?? string.Empty).TrimEnd('/');
            _apiKey = apiKey ?? string.Empty;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_url) && !string.IsNullOrWhiteSpace(_apiKey);

        private static string ObjectName(TemplateKind kind) => kind switch
        {
            TemplateKind.Presupuesto => "presupuesto.docx",
            TemplateKind.OF => "of.docx",
            TemplateKind.OT => "ot.docx",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        private static string CachePath(TemplateKind kind) =>
            Path.Combine(AppPaths.TemplatesCacheFolder, ObjectName(kind));

        private HttpRequestMessage NewRequest(HttpMethod method, string relativePath)
        {
            var req = new HttpRequestMessage(method, $"{_url}/storage/v1/{relativePath}");
            req.Headers.Add("apikey", _apiKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return req;
        }

        public async Task PublishTemplateAsync(TemplateKind kind, string localFilePath)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("El almacenamiento de plantillas en la nube no está configurado (falta Url/AnonKey de Supabase).");
            if (!File.Exists(localFilePath))
                throw new FileNotFoundException("No se encontró el archivo de plantilla a publicar.", localFilePath);

            byte[] bytes = await File.ReadAllBytesAsync(localFilePath);

            using var req = NewRequest(HttpMethod.Post, $"object/{Bucket}/{ObjectName(kind)}");
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
            }
            catch (Exception ex) { AppLog.Warning(ex, "No se pudo actualizar el cache local de plantillas tras publicar"); }

            AppLog.Information("Plantilla {Kind} publicada en Supabase Storage ({Bytes} bytes)", kind, bytes.Length);
        }

        public async Task<string?> ResolveTemplateAsync(TemplateKind kind)
        {
            string cache = CachePath(kind);
            if (!IsConfigured)
                return File.Exists(cache) ? cache : null;

            try
            {
                using var req = NewRequest(HttpMethod.Get, $"object/{Bucket}/{ObjectName(kind)}");
                using var resp = await _http.SendAsync(req);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return File.Exists(cache) ? cache : null;

                resp.EnsureSuccessStatusCode();
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync();

                Directory.CreateDirectory(AppPaths.TemplatesCacheFolder);
                // Escritura atómica: si la app se cierra a mitad de descarga no queda un docx corrupto.
                string tmp = cache + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, cache, overwrite: true);
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
                using var req = NewRequest(HttpMethod.Post, $"object/list/{Bucket}");
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
