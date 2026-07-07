using System;
using System.Threading.Tasks;

namespace Alquitel.Core.Interfaces
{
    /// <summary>Tipo de plantilla documental. Cada tipo mapea a un objeto fijo del bucket remoto.</summary>
    public enum TemplateKind
    {
        Presupuesto = 0,
        OF = 1,
        OT = 2
    }

    /// <summary>Estado de una plantilla publicada en la nube.</summary>
    public record TemplateCloudStatus(TemplateKind Kind, bool Exists, DateTime? UpdatedAt, long? SizeBytes);

    /// <summary>
    /// Almacenamiento centralizado de plantillas .docx en la nube (Supabase Storage).
    /// El Admin publica la plantilla desde Configuración; todos los puestos de trabajo
    /// la descargan automáticamente al generar documentos, con cache local para poder
    /// seguir trabajando sin internet (última versión descargada).
    /// </summary>
    public interface ITemplateStorageService
    {
        /// <summary>True cuando hay Url + AnonKey de Supabase configuradas.</summary>
        bool IsConfigured { get; }

        /// <summary>Sube (upsert) el archivo local como plantilla oficial del tipo dado.</summary>
        Task PublishTemplateAsync(TemplateKind kind, string localFilePath);

        /// <summary>
        /// Devuelve la ruta local de la plantilla lista para usar: descarga la versión
        /// del servidor si hay conexión, o la última copia cacheada si no la hay.
        /// Null cuando no hay plantilla publicada ni cache (usar plantilla local clásica).
        /// </summary>
        Task<string?> ResolveTemplateAsync(TemplateKind kind);

        /// <summary>Consulta si existe plantilla publicada y su fecha de actualización.</summary>
        Task<TemplateCloudStatus> GetStatusAsync(TemplateKind kind);
    }
}
