using System;
using System.Diagnostics;
using System.IO;
using Alquitel.Infrastructure;

namespace Alquitel.UI.Helpers
{
    /// <summary>
    /// Punto único para abrir cosas con el shell de Windows.
    ///
    /// Los nombres de archivo de la app se arman con la razón social del cliente y el
    /// nombre del evento — datos que escribe el usuario. Pasarlos concatenados dentro de
    /// una línea de comandos (<c>Process.Start("explorer.exe", $"/select,\"{path}\"")</c>)
    /// dejaba que una comilla en el nombre cerrara el argumento y colara otro.
    /// <see cref="ProcessStartInfo.ArgumentList"/> entrega cada argumento por separado y
    /// se encarga del escapado.
    /// </summary>
    public static class ShellLauncher
    {
        /// <summary>Abre el Explorador con el archivo seleccionado.</summary>
        public static void RevealInExplorer(string? fullPath)
        {
            try
            {
                var canonical = PathValidator.Canonicalize(fullPath);
                if (canonical == null || !File.Exists(canonical))
                {
                    AppLog.Warning("RevealInExplorer: ruta inexistente o inválida {Path}", fullPath);
                    return;
                }

                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("/select," + canonical);
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "RevealInExplorer failed for {Path}", fullPath);
            }
        }

        /// <summary>
        /// Abre un documento con su aplicación asociada, exigiendo que esté dentro de
        /// <paramref name="allowedRoot"/>. Devuelve false si la ruta no pasa el control.
        /// </summary>
        public static bool OpenDocument(string? fullPath, string? allowedRoot)
        {
            if (!PathValidator.IsDocxWithinRoot(fullPath, allowedRoot))
            {
                AppLog.Warning("OpenDocument rechazado: ruta fuera de la carpeta permitida {Path}", fullPath);
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo(fullPath!) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "OpenDocument failed for {Path}", fullPath);
                return false;
            }
        }
    }
}
