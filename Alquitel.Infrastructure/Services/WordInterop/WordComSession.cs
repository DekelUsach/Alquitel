using System.Runtime.InteropServices;

namespace Alquitel.Infrastructure.Services.WordInterop;

public interface IWordComSessionFactory
{
    IWordComSession Create();
}

public interface IWordComSession : IDisposable
{
    dynamic? WordApp { get; }
    dynamic? Document { get; }
    void Initialize();
    void OpenTemplate(string templatePath);
    void SaveWorkingCopy(string outputPath);
    void ExportAsPdf(string pdfPath);
}

public sealed class WordComSessionFactory : IWordComSessionFactory
{
    public IWordComSession Create() => new WordComSession();
}

public sealed class WordComSession : IWordComSession
{
    private string? _tempPath;
    private bool _disposed;

    public dynamic? WordApp { get; private set; }
    public dynamic? Document { get; private set; }
    public void Initialize()
    {
        ThrowIfDisposed();
        SweepStaleTempFiles();
        Type? wordType = Type.GetTypeFromProgID("Word.Application");
        if (wordType == null)
            throw new InvalidOperationException(
                "Microsoft Word no está instalado o no está registrado correctamente en este sistema.");

        WordApp = Activator.CreateInstance(wordType)
            ?? throw new InvalidOperationException("No se pudo iniciar Microsoft Word.");

        WordApp.Visible = false;
        WordApp.DisplayAlerts = 0;
        WordApp.AutomationSecurity = 3;
    }

    public void OpenTemplate(string templatePath)
    {
        ThrowIfDisposed();
        if (WordApp == null)
            throw new InvalidOperationException("La sesión de Word no fue inicializada.");

        var workDirectory = Path.Combine(AppPaths.AppDataRoot, "DocumentWork");
        Directory.CreateDirectory(workDirectory);
        _tempPath = Path.Combine(workDirectory, $"alquitel_word_{Guid.NewGuid():N}.docx");
        File.Copy(templatePath, _tempPath, overwrite: false);

        var attrs = File.GetAttributes(_tempPath);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(_tempPath, attrs & ~FileAttributes.ReadOnly);

        // No se eliminan lockfiles de Word ni se modifica Protected View. La plantilla
        // ya fue validada y se abre desde una copia propia sin actualizar vínculos.
        Document = WordApp.Documents.Open(
            _tempPath,
            ConfirmConversions: false,
            ReadOnly: false,
            AddToRecentFiles: false,
            Revert: false,
            UpdateLinks: 0,
            Visible: false,
            OpenAndRepair: false,
            NoEncodingDialog: true);

        if (Document == null)
            throw new InvalidOperationException("Word no pudo abrir la copia de trabajo de la plantilla.");

        try
        {
            if ((int)Document.ProtectionType != -1)
                throw new DocumentTemplateValidationException(
                    "La plantilla está protegida. Guardá una copia editable antes de usarla en Alquitel.");
        }
        catch (DocumentTemplateValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DocumentTemplateValidationException(
                "No se pudo comprobar si la plantilla permite edición segura.", ex);
        }
    }

    public void SaveWorkingCopy(string outputPath)
    {
        ThrowIfDisposed();
        if (Document == null)
            throw new InvalidOperationException("No hay un documento abierto para guardar.");
        Document.SaveAs2(outputPath, FileFormat: 16, AddToRecentFiles: false);
    }

    public void ExportAsPdf(string pdfPath)
    {
        ThrowIfDisposed();
        if (Document == null)
            throw new InvalidOperationException("No hay un documento abierto para exportar.");

        Document.ExportAsFixedFormat(
            OutputFileName: pdfPath,
            ExportFormat: 17,
            OpenAfterExport: false,
            OptimizeFor: 0,
            Range: 0,
            Item: 0,
            IncludeDocProps: true,
            KeepIRM: true,
            CreateBookmarks: 0,
            DocStructureTags: true,
            BitmapMissingFonts: true,
            UseISO19005_1: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Document != null)
        {
            try { Document.Close(false); }
            catch (Exception ex)
            {
                AppLog.Warning("No se pudo cerrar el documento de Word ({ErrorType}, 0x{HResult:X8})", ex.GetType().Name, ex.HResult);
            }
            ReleaseComObject(Document, "documento");
            Document = null;
        }
        if (WordApp != null)
        {
            try { WordApp.Quit(); }
            catch (Exception ex)
            {
                AppLog.Warning("No se pudo cerrar la instancia de Word ({ErrorType}, 0x{HResult:X8})", ex.GetType().Name, ex.HResult);
            }
            ReleaseComObject(WordApp, "aplicación Word");
            WordApp = null;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        DocumentGenerationSafety.TryDelete(_tempPath);
        _tempPath = null;
    }

    private static void ReleaseComObject(object value, string kind)
    {
        try
        {
            if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "No se pudo liberar la referencia COM de {Kind} ({ErrorType}, 0x{HResult:X8})",
                kind, ex.GetType().Name, ex.HResult);
        }
    }

    private static void SweepStaleTempFiles()
    {
        try
        {
            var workDirectory = Path.Combine(AppPaths.AppDataRoot, "DocumentWork");
            if (!Directory.Exists(workDirectory)) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(workDirectory, "alquitel_word_*.docx"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch
                {
                    // Se reintentará en la próxima sesión.
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "No se pudieron limpiar copias de trabajo documentales antiguas ({ErrorType})",
                ex.GetType().Name);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
