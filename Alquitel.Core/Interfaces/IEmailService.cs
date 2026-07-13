using System.Collections.Generic;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Creación de borradores de correo con adjuntos. La implementación usa Outlook
    /// (COM, ya que Office está instalado para el motor de Word): abre la ventana de
    /// redacción con el documento adjunto y el usuario revisa y envía manualmente.
    /// </summary>
    public interface IEmailService
    {
        /// <summary>True si hay un cliente de correo automatizable (Outlook) instalado.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Abre un borrador de correo (no lo envía). Lanza InvalidOperationException con
        /// mensaje legible si Outlook no está disponible.
        /// </summary>
        void CreateDraft(string? to, string subject, string body, IEnumerable<string> attachmentPaths);
    }
}
