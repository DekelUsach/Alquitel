using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Alquitel.Infrastructure.Services.WordInterop
{
    public class WordComSession : IDisposable
    {
        public dynamic? WordApp { get; private set; }
        public dynamic? Document { get; private set; }
        private string? _tempPath;

        public void Initialize()
        {
            Type? wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
                throw new Exception("Microsoft Word no está instalado o no está registrado correctamente en este sistema.");

            WordApp = Activator.CreateInstance(wordType);
            if (WordApp == null)
                throw new Exception("No se pudo iniciar la instancia de Microsoft Word.");

            WordApp.Visible = false;
            WordApp.DisplayAlerts = 0;       // wdAlertsNone
            WordApp.AutomationSecurity = 3;  // msoAutomationSecurityForceDisable
        }

        public void OpenTemplate(string templatePath)
        {
            string templateDir  = Path.GetDirectoryName(templatePath)!;
            string templateFile = Path.GetFileName(templatePath);
            string lockFile     = Path.Combine(templateDir, "~$" + templateFile);
            try { if (File.Exists(lockFile)) File.Delete(lockFile); } catch { }

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
            catch { }

            Document = WordApp!.Documents.Open(_tempPath, ReadOnly: false, AddToRecentFiles: false, ConfirmConversions: false);

            if (WordApp.ProtectedViewWindows.Count > 0)
            {
                try
                {
                    dynamic pvw = WordApp.ProtectedViewWindows[1];
                    Document = pvw.Edit();
                }
                catch { }
            }

            try
            {
                if ((int)Document!.ProtectionType != -1) // -1 = wdNoProtection
                    Document.Unprotect(Password: "");
            }
            catch { }
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
            if (_tempPath != null)
            {
                try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
            }

            if (Document != null)
            {
                try { Document.Close(false); } catch { }
                Marshal.ReleaseComObject(Document);
            }
            if (WordApp != null)
            {
                try { WordApp.Quit(); } catch { }
                Marshal.ReleaseComObject(WordApp);
            }
        }
    }
}
