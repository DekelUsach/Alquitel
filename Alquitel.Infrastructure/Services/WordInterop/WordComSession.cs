using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Alquitel.Infrastructure.Services.WordInterop
{
    public class WordComSession : IDisposable
    {
        public dynamic? WordApp { get; private set; }
        public dynamic? Document { get; private set; }
        private string? _tempPath;

        // PID del WINWORD.EXE lanzado por esta sesión: red de seguridad para poder
        // matarlo si Quit() no alcanza (RCWs intermedios vivos, diálogo colgado, etc.).
        private int? _wordPid;

        /// <summary>
        /// PID de la última sesión iniciada. WordDocumentService lo usa en el camino de
        /// timeout, donde el hilo STA quedó bloqueado y nunca ejecuta este Dispose.
        /// La app genera de a un documento por vez, así que un único slot alcanza.
        /// </summary>
        public static int? LastLaunchedPid { get; private set; }

        private static HashSet<int> GetWinwordPids() =>
            System.Diagnostics.Process.GetProcessesByName("WINWORD")
                .Select(p => { int id = p.Id; p.Dispose(); return id; })
                .ToHashSet();

        public void Initialize()
        {
            Type? wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
                throw new Exception("Microsoft Word no está instalado o no está registrado correctamente en este sistema.");

            var pidsBefore = GetWinwordPids();

            WordApp = Activator.CreateInstance(wordType);
            if (WordApp == null)
                throw new Exception("No se pudo iniciar la instancia de Microsoft Word.");

            try
            {
                // Solo se adopta el PID si apareció EXACTAMENTE un WINWORD nuevo. Si el
                // usuario abrió Word a mano en el mismo instante habría dos candidatos y
                // elegir al azar terminaría matándole el documento con trabajo sin guardar.
                var newPids = GetWinwordPids().Except(pidsBefore).ToList();
                if (newPids.Count == 1)
                {
                    _wordPid = newPids[0];
                }
                else if (newPids.Count > 1)
                {
                    _wordPid = null;
                    AppLog.Warning("Aparecieron {Count} procesos WINWORD nuevos: no se adopta ninguno para no matar el Word del usuario", newPids.Count);
                }
                LastLaunchedPid = _wordPid;
            }
            catch (Exception ex) { AppLog.Warning(ex, "Could not capture WINWORD PID"); }

            WordApp.Visible = false;
            WordApp.DisplayAlerts = 0;       // wdAlertsNone
            WordApp.AutomationSecurity = 3;  // msoAutomationSecurityForceDisable
        }

        public void OpenTemplate(string templatePath)
        {
            string templateDir  = Path.GetDirectoryName(templatePath)!;
            string templateFile = Path.GetFileName(templatePath);
            string lockFile     = Path.Combine(templateDir, "~$" + templateFile);
            try { if (File.Exists(lockFile)) File.Delete(lockFile); } catch (Exception ex) { AppLog.Warning(ex, "Could not delete lock file {LockFile}", lockFile); }

            _tempPath = Path.Combine(Path.GetTempPath(), $"alquitel_tmp_{Guid.NewGuid():N}.docx");
            File.Copy(templatePath, _tempPath, overwrite: true);

            var attrs = File.GetAttributes(_tempPath);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(_tempPath, attrs & ~FileAttributes.ReadOnly);

            try
            {
                WordApp!.Options.DisableHardwareGraphicsAcceleration = true;
                dynamic pvOptions = WordApp.Options.ProtectedViewOptions;
                pvOptions.OpenUnsafeLocationsInProtectedView  = false;
                pvOptions.OpenFilesFromInternetInProtectedView = false;
                pvOptions.OpenFilesInUnsafeLocationsInProtectedView = false;
            }
            catch (Exception ex) { AppLog.Warning(ex, "Failed to set Word options/ProtectedViewOptions"); }

            Document = WordApp!.Documents.Open(_tempPath, ReadOnly: false, AddToRecentFiles: false, ConfirmConversions: false);

            if (WordApp.ProtectedViewWindows.Count > 0)
            {
                try
                {
                    dynamic pvw = WordApp.ProtectedViewWindows[1];
                    Document = pvw.Edit();
                }
                catch (Exception ex) { AppLog.Warning(ex, "Failed to edit ProtectedViewWindow"); }
            }

            try
            {
                if ((int)Document!.ProtectionType != -1) // -1 = wdNoProtection
                    Document.Unprotect(Password: "");
            }
            catch (Exception ex) { AppLog.Warning(ex, "Failed to unprotect document"); }
        }

        public void SaveAndClose(string outputPath)
        {
            if (Document != null)
            {
                Document.SaveAs2(outputPath);
            }
        }

        public void ExportAsPdf(string pdfPath)
        {
            if (Document != null)
            {
                // 17 = wdExportFormatPDF
                Document.ExportAsFixedFormat(
                    OutputFileName: pdfPath,
                    ExportFormat: 17,
                    OpenAfterExport: false,
                    OptimizeFor: 0, // wdExportOptimizeForPrint
                    Range: 0, // wdExportAllDocument
                    Item: 0, // wdExportDocumentContent
                    IncludeDocProps: true,
                    KeepIRM: true,
                    CreateBookmarks: 0, // wdExportCreateNoBookmarks
                    DocStructureTags: true,
                    BitmapMissingFonts: true,
                    UseISO19005_1: false
                );
            }
        }

        public void Dispose()
        {
            // Release COM objects FIRST — Word holds the temp file open, so deleting
            // it before Close/Quit always fails and temp files pile up in %TEMP%.
            if (Document != null)
            {
                try { Document.Close(false); } catch (Exception ex) { AppLog.Warning(ex, "Failed to close document"); }
                try { Marshal.ReleaseComObject(Document); } catch (Exception ex) { AppLog.Warning(ex, "Failed to release document COM object"); }
                Document = null;
            }
            if (WordApp != null)
            {
                try { WordApp.Quit(); } catch (Exception ex) { AppLog.Warning(ex, "Failed to quit Word application"); }
                try { Marshal.ReleaseComObject(WordApp); } catch (Exception ex) { AppLog.Warning(ex, "Failed to release Word COM object"); }
                WordApp = null;
            }

            // El pipeline (PlaceholderReplacer/ProductRenderer) crea RCWs intermedios vía
            // dynamic (StoryRanges, Find, Tables, Shapes...) imposibles de liberar uno a
            // uno. Mientras el CLR los retenga, WINWORD.EXE puede sobrevivir a Quit():
            // forzar la colección finaliza esos RCWs y suelta sus referencias COM.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Red de seguridad final: si el proceso de esta sesión sigue vivo, matarlo
            // para no acumular WINWORD huérfanos en el equipo del usuario.
            if (_wordPid is int pid)
            {
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(pid);
                    if (!proc.HasExited && proc.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!proc.WaitForExit(3000))
                        {
                            proc.Kill();
                            AppLog.Warning("WINWORD huérfano (PID {Pid}) terminado a la fuerza tras Quit()", pid);
                        }
                    }
                }
                catch (ArgumentException) { /* el proceso ya salió */ }
                catch (Exception ex) { AppLog.Warning(ex, "No se pudo verificar/terminar WINWORD PID {Pid}", pid); }
                _wordPid = null;
            }

            if (_tempPath != null)
            {
                try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch (Exception ex) { AppLog.Warning(ex, "Failed to delete temp file {TempPath}", _tempPath); }
                _tempPath = null;
            }

            // Barrido de temporales de corridas anteriores: si el proceso murió a mitad
            // (crash, timeout, apagón) quedaron .docx con datos de clientes en %TEMP%.
            SweepStaleTempFiles();
        }

        /// <summary>
        /// Borra los alquitel_tmp_*.docx de más de un día que hayan quedado en %TEMP%
        /// por corridas abortadas. Best-effort y silencioso: nunca debe romper el flujo.
        /// </summary>
        private static void SweepStaleTempFiles()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "alquitel_tmp_*.docx"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                    }
                    catch { /* archivo en uso o sin permisos: se intentará la próxima vez */ }
                }
            }
            catch (Exception ex) { AppLog.Warning(ex, "No se pudo barrer temporales viejos de Word"); }
        }
    }
}
