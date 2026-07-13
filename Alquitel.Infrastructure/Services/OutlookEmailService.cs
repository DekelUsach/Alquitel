using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Alquitel.Core.Interfaces;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Implementación de <see cref="IEmailService"/> sobre Outlook COM (late binding).
    /// Crea el MailItem, adjunta los documentos y lo muestra para revisión manual:
    /// nunca envía solo. Cada referencia COM se libera con Marshal.ReleaseComObject
    /// para no dejar procesos OUTLOOK.EXE huérfanos (misma regla que Word Interop).
    /// </summary>
    public class OutlookEmailService : IEmailService
    {
        public bool IsAvailable => Type.GetTypeFromProgID("Outlook.Application") != null;

        public void CreateDraft(string? to, string subject, string body, IEnumerable<string> attachmentPaths)
        {
            var outlookType = Type.GetTypeFromProgID("Outlook.Application");
            if (outlookType == null)
                throw new InvalidOperationException(
                    "Outlook no está instalado en este equipo: adjuntá el documento manualmente desde tu cliente de correo.");

            object? app = null;
            object? mail = null;
            try
            {
                app = Activator.CreateInstance(outlookType)
                    ?? throw new InvalidOperationException("No se pudo iniciar Outlook.");
                dynamic outlook = app;
                mail = outlook.CreateItem(0); // 0 = olMailItem
                dynamic mailItem = mail!;

                if (!string.IsNullOrWhiteSpace(to)) mailItem.To = to;
                mailItem.Subject = subject;
                mailItem.Body = body;

                foreach (var path in attachmentPaths)
                {
                    if (File.Exists(path))
                        mailItem.Attachments.Add(path);
                }

                mailItem.Display(false); // ventana de redacción, no modal: el usuario revisa y envía
                AppLog.Information("Email draft created with subject {Subject}", subject);
            }
            catch (COMException ex)
            {
                AppLog.Error(ex, "Outlook COM failed while creating email draft");
                throw new InvalidOperationException(
                    "Outlook no respondió al crear el borrador. Abrilo manualmente y reintentá.", ex);
            }
            finally
            {
                // El MailItem queda vivo en la ventana de Outlook; solo se liberan los RCW locales.
                if (mail != null) Marshal.ReleaseComObject(mail);
                if (app != null) Marshal.ReleaseComObject(app);
            }
        }
    }
}
