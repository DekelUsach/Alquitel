using System.Drawing;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Security;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Win32.SafeHandles;

namespace Alquitel.Infrastructure.Services;

public sealed class DocumentTemplateValidationException : IOException
{
    public DocumentTemplateValidationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed record ValidatedDocumentRequest(
    string TemplatePath,
    string RequestedOutputPath,
    string TemplateId,
    IReadOnlyList<string> Warnings);

internal static class DocumentGenerationSafety
{
    private const long MaxTemplateBytes = 50L * 1024 * 1024;
    private const long MaxExpandedBytes = 200L * 1024 * 1024;
    private const long MaxImageBytes = 10L * 1024 * 1024;
    private const long MaxImagePixels = 40_000_000;
    internal static Action<string>? ConditionalDeleteTestHook = null;

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const int FileDispositionInfoClass = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.I1)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    public static ValidatedDocumentRequest Validate(
        string templatePath,
        string outputPath,
        bool allowLegacyProductBookmark,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var templateSource = Canonicalize(templatePath, "La ruta de la plantilla no es válida.");
        var output = Canonicalize(outputPath, "La ruta de salida no es válida.");

        if (!string.Equals(Path.GetExtension(templateSource), ".docx", StringComparison.OrdinalIgnoreCase))
            throw new DocumentTemplateValidationException("La plantilla debe ser un archivo .docx sin macros.");
        if (!string.Equals(Path.GetExtension(output), ".docx", StringComparison.OrdinalIgnoreCase))
            throw new DocumentTemplateValidationException("El documento de salida debe tener extensión .docx.");
        if (!File.Exists(templateSource))
            throw new FileNotFoundException("No se encontró la plantilla.", templateSource);
        if (string.Equals(templateSource, output, StringComparison.OrdinalIgnoreCase))
            throw new DocumentTemplateValidationException("La plantilla y el documento de salida no pueden ser el mismo archivo.");

        var info = new FileInfo(templateSource);
        if (info.Length == 0 || info.Length > MaxTemplateBytes)
            throw new DocumentTemplateValidationException("La plantilla está vacía o supera el límite seguro de 50 MB.");

        var outputDirectory = Path.GetDirectoryName(output)
            ?? throw new DocumentTemplateValidationException("La ruta de salida no tiene una carpeta válida.");
        Directory.CreateDirectory(outputDirectory);
        SweepStaleStagingFiles(outputDirectory);
        RecoverPublicationJournals(outputDirectory, cancellationToken);
        output = Path.Combine(outputDirectory, SanitizeFileName(Path.GetFileName(output)));

        var warnings = new List<string>();
        string? template = null;
        try
        {
            template = CreatePrivateTemplateSnapshot(templateSource, cancellationToken);
            ValidatePackageSize(template, cancellationToken);
            using var document = WordprocessingDocument.Open(template, false);
            var main = document.MainDocumentPart;
            var body = main?.Document?.Body;
            if (body == null)
                throw new DocumentTemplateValidationException("La plantilla está dañada: no contiene un documento principal válido.");

            var allText = new StringBuilder(body.InnerText);
            foreach (var header in main!.HeaderParts)
                allText.Append(header.Header?.InnerText);
            foreach (var footer in main.FooterParts)
                allText.Append(footer.Footer?.InnerText);

            var bookmarkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bookmark in main.Document!.Descendants<DocumentFormat.OpenXml.Wordprocessing.BookmarkStart>())
                if (!string.IsNullOrWhiteSpace(bookmark.Name?.Value)) bookmarkNames.Add(bookmark.Name!.Value!);
            foreach (var header in main.HeaderParts)
                if (header.Header != null)
                    foreach (var bookmark in header.Header.Descendants<DocumentFormat.OpenXml.Wordprocessing.BookmarkStart>())
                        if (!string.IsNullOrWhiteSpace(bookmark.Name?.Value)) bookmarkNames.Add(bookmark.Name!.Value!);
            foreach (var footer in main.FooterParts)
                if (footer.Footer != null)
                    foreach (var bookmark in footer.Footer.Descendants<DocumentFormat.OpenXml.Wordprocessing.BookmarkStart>())
                        if (!string.IsNullOrWhiteSpace(bookmark.Name?.Value)) bookmarkNames.Add(bookmark.Name!.Value!);

            var templateText = allText.ToString();
            var hasTextMarker = body.InnerText.Contains("{{PRODUCTOS_AQUI}}", StringComparison.Ordinal);
            var hasLegacyBookmark = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.BookmarkStart>()
                .Any(bookmark => string.Equals(
                    bookmark.Name?.Value,
                    "BK_EQUIPMENT_TABLE",
                    StringComparison.OrdinalIgnoreCase));
            if (!hasTextMarker && !(allowLegacyProductBookmark && hasLegacyBookmark))
            {
                var accepted = allowLegacyProductBookmark
                    ? "{{PRODUCTOS_AQUI}} o el marcador BK_EQUIPMENT_TABLE"
                    : "{{PRODUCTOS_AQUI}}";
                throw new DocumentTemplateValidationException(
                    $"La plantilla no contiene el marcador obligatorio {accepted}.");
            }

            AddMissingPlaceholderWarnings(templateText, bookmarkNames, warnings);

            ValidateRelationships(main, warnings, cancellationToken);
        }
        catch (DocumentTemplateValidationException)
        {
            TryDelete(template);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryDelete(template);
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDelete(template);
            throw new DocumentTemplateValidationException(
                "La plantilla está dañada, bloqueada o no es un documento OpenXML válido.", ex);
        }

        return new ValidatedDocumentRequest(
            template!, output, SafeTemplateId(templateSource), warnings);
    }

    public static bool TryCreateImageSnapshot(
        string? imagePath,
        out DocumentImageSnapshot? snapshot,
        out string warning)
    {
        snapshot = null;
        warning = string.Empty;
        if (string.IsNullOrWhiteSpace(imagePath)) return false;

        string? privatePath = null;
        try
        {
            var path = Path.GetFullPath(imagePath);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp"))
            {
                warning = "Se omitió una imagen de producto con formato no permitido.";
                return false;
            }

            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxImageBytes)
            {
                warning = "Se omitió una imagen de producto inexistente, vacía o demasiado grande.";
                return false;
            }

            var workDirectory = GetDocumentWorkDirectory();
            privatePath = Path.Combine(workDirectory, $"alquitel_image_{Guid.NewGuid():N}{extension}");
            CopyBoundedWithFlush(path, privatePath, MaxImageBytes);

            using var stream = new FileStream(privatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (image.Width <= 0 || image.Height <= 0 ||
                image.Width > 10_000 || image.Height > 10_000 ||
                (long)image.Width * image.Height > MaxImagePixels)
            {
                warning = "Se omitió una imagen de producto con dimensiones fuera del límite seguro.";
                image.Dispose();
                stream.Close();
                TryDelete(privatePath);
                return false;
            }

            snapshot = new DocumentImageSnapshot(privatePath);
            privatePath = null;
            return true;
        }
        catch
        {
            TryDelete(privatePath);
            warning = "Se omitió una imagen de producto inválida o dañada.";
            return false;
        }
    }

    public static string CreateStagingPath(string requestedOutputPath, string extension)
    {
        var directory = Path.GetDirectoryName(requestedOutputPath)!;
        return Path.Combine(directory, $".alquitel-{Guid.NewGuid():N}.tmp{extension}");
    }

    public static PublishedDocument Publish(
        string stagedDocumentPath,
        string? stagedPdfPath,
        string requestedOutputPath,
        CancellationToken cancellationToken,
        Action? onCommitStarting = null)
    {
        var directory = Path.GetDirectoryName(requestedOutputPath)!;
        using var lease = AcquirePublishLease(directory, cancellationToken);
        var stem = Path.GetFileNameWithoutExtension(requestedOutputPath);

        for (var version = 1; version <= 10_000; version++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = version == 1 ? string.Empty : $" ({version})";
            var documentPath = Path.Combine(directory, $"{stem}{suffix}.docx");
            var companionPdfPath = Path.Combine(directory, $"{stem}{suffix}.pdf");
            var pdfPath = stagedPdfPath == null
                ? null
                : companionPdfPath;
            if (File.Exists(documentPath) || File.Exists(companionPdfPath))
                continue;

            string? journalPath = null;
            var pdfMoved = false;
            try
            {
                if (stagedPdfPath != null)
                {
                    onCommitStarting?.Invoke();
                    journalPath = WritePublicationJournal(
                        directory, documentPath, pdfPath!, stagedPdfPath);
                    File.Move(stagedPdfPath, pdfPath!, overwrite: false);
                    pdfMoved = true;
                }
                else
                {
                    onCommitStarting?.Invoke();
                }

                // El DOCX es el marcador de commit: los exploradores de documentos no
                // ven una generación completa hasta que este movimiento atómico sucede.
                File.Move(stagedDocumentPath, documentPath, overwrite: false);
                TryDelete(journalPath);
                return new PublishedDocument(documentPath, pdfPath);
            }
            catch (IOException) when (
                File.Exists(stagedDocumentPath) &&
                (File.Exists(documentPath) || (pdfPath != null && File.Exists(pdfPath))))
            {
                if (pdfMoved)
                    File.Move(pdfPath!, stagedPdfPath!, overwrite: false);
                TryDelete(journalPath);
                continue;
            }
            catch
            {
                if (pdfMoved) TryDelete(pdfPath);
                TryDelete(journalPath);
                throw;
            }
        }

        throw new IOException("No se pudo reservar un nombre de documento libre en la carpeta de salida.");
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "No se pudo limpiar un archivo temporal del generador documental ({ErrorType})",
                ex.GetType().Name);
        }
    }

    public static string SafeTemplateId(string templatePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(templatePath));
        return Convert.ToHexString(bytes.AsSpan(0, 6));
    }

    private static string CreatePrivateTemplateSnapshot(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workDirectory = GetDocumentWorkDirectory();
        var snapshotPath = Path.Combine(
            workDirectory, $"alquitel_template_{Guid.NewGuid():N}.docx");
        try
        {
            CopyBoundedWithFlush(sourcePath, snapshotPath, MaxTemplateBytes);
            return snapshotPath;
        }
        catch
        {
            TryDelete(snapshotPath);
            throw;
        }
    }

    private static string GetDocumentWorkDirectory()
    {
        var directory = Path.Combine(AppPaths.AppDataRoot, "DocumentWork");
        Directory.CreateDirectory(directory);
        SweepStalePrivateSnapshots(directory);
        return directory;
    }

    private static void CopyBoundedWithFlush(string sourcePath, string destinationPath, long maxBytes)
    {
        using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.WriteThrough);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total = checked(total + read);
            if (total > maxBytes)
                throw new DocumentTemplateValidationException("El archivo supera el límite seguro permitido.");
            destination.Write(buffer, 0, read);
        }
        destination.Flush(flushToDisk: true);
    }

    private static string Canonicalize(string path, string error)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new DocumentTemplateValidationException(error);
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DocumentTemplateValidationException(error, ex);
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            stem = stem.Replace(invalid, '_');
        stem = stem.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "documento";
        if (stem.Length > 120) stem = stem[..120].TrimEnd();
        return $"{stem}.docx";
    }

    private static void ValidatePackageSize(string templatePath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(templatePath);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes)
                throw new DocumentTemplateValidationException("La plantilla supera el límite seguro de contenido expandido.");
        }

        var contentTypes = archive.GetEntry("[Content_Types].xml");
        if (contentTypes == null)
            throw new DocumentTemplateValidationException("La plantilla está dañada: faltan tipos de contenido.");
        using var reader = new StreamReader(contentTypes.Open(), Encoding.UTF8, true, leaveOpen: false);
        var xml = reader.ReadToEnd();
        if (xml.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) ||
            xml.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
            throw new DocumentTemplateValidationException("La plantilla contiene macros y no está permitida.");
    }

    private static void ValidateRelationships(
        OpenXmlPart root,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<OpenXmlPart>();
        var seen = new HashSet<OpenXmlPart>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = pending.Pop();
            if (!seen.Add(part)) continue;
            ValidateFieldInstructions(part);

            var unsafePartName = part.GetType().Name;
            if (unsafePartName is "VbaProjectPart" or "EmbeddedObjectPart" or
                "EmbeddedPackagePart" or "ActiveXControlBinaryPart" or "ControlPropertiesPart")
            {
                throw new DocumentTemplateValidationException(
                    "La plantilla contiene macros, controles u objetos incrustados no permitidos.");
            }

            foreach (var relationship in part.ExternalRelationships)
            {
                if (relationship.RelationshipType.EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase))
                {
                    if (!warnings.Contains("La plantilla contiene hipervínculos externos; no se actualizarán automáticamente."))
                        warnings.Add("La plantilla contiene hipervínculos externos; no se actualizarán automáticamente.");
                    continue;
                }

                throw new DocumentTemplateValidationException(
                    "La plantilla contiene vínculos externos activos y no es segura para automatización.");
            }

            foreach (var child in part.Parts)
                pending.Push(child.OpenXmlPart);
        }
    }

    private static void ValidateFieldInstructions(OpenXmlPart part)
    {
        if (part.RootElement == null) return;
        var instructions = part.RootElement
            .Descendants<DocumentFormat.OpenXml.Wordprocessing.FieldCode>()
            .Select(field => field.Text)
            .Concat(part.RootElement
                .Descendants<DocumentFormat.OpenXml.Wordprocessing.SimpleField>()
                .Select(field => field.Instruction?.Value ?? string.Empty));

        foreach (var instruction in instructions)
        {
            var command = instruction.TrimStart()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (command is not null && new[]
                {
                    "DDE", "DDEAUTO", "INCLUDETEXT", "INCLUDEPICTURE", "LINK",
                }.Contains(command, StringComparer.OrdinalIgnoreCase))
            {
                throw new DocumentTemplateValidationException(
                    $"La plantilla contiene el campo externo no permitido {command.ToUpperInvariant()}.");
            }
        }
    }

    private static void AddMissingPlaceholderWarnings(
        string templateText,
        IReadOnlySet<string> bookmarkNames,
        ICollection<string> warnings)
    {
        AddWarningWhenMissing(
            "cliente",
            new[] { "[CLIENTE]", "{{CLIENTE}}", "<<CLIENTE>>", "(nombre cliente)" },
            "BK_CLIENT_NAME");
        AddWarningWhenMissing(
            "número de presupuesto",
            new[] { "(nro presupuesto)", "[NUMERO]", "{{NUMERO}}", "[PRESUPUESTO]" },
            "BK_BUDGET_NUM");
        AddWarningWhenMissing(
            "fecha",
            new[] { "(fecha actual)", "(fecha)", "[FECHA]", "{{FECHA}}", "(FECHA_EVENTO)", "{{FECHA_EVENTO}}" },
            "BK_DATE");

        void AddWarningWhenMissing(string label, IEnumerable<string> markers, string bookmark)
        {
            if (markers.Any(marker => templateText.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
                bookmarkNames.Contains(bookmark))
                return;
            warnings.Add($"La plantilla no contiene un marcador reconocido para {label}.");
        }
    }

    private static FileStream AcquirePublishLease(string directory, CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(directory, ".alquitel-document-publish.lock");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(40);
            }
        }
    }

    private static string WritePublicationJournal(
        string directory,
        string documentPath,
        string pdfPath,
        string stagedPdfPath)
    {
        var workDirectory = GetDocumentWorkDirectory();
        var journalPath = Path.Combine(
            workDirectory, $"alquitel_publish_{Guid.NewGuid():N}.journal");
        var temporaryPath = journalPath + ".tmp";
        try
        {
            var payload = new PublicationJournal(
                Path.GetFullPath(directory),
                Path.GetFileName(documentPath),
                Path.GetFileName(pdfPath),
                ComputeFileSha256(stagedPdfPath));
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
            var protectedBytes = DpapiProtector.Protect(plaintext);
            using (var stream = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write("ALQJP1\n"u8);
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, journalPath, overwrite: false);
            return journalPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(journalPath);
            throw;
        }
    }

    private static void RecoverPublicationJournals(
        string directory,
        CancellationToken cancellationToken)
    {
        using var lease = AcquirePublishLease(directory, cancellationToken);
        var normalizedDirectory = Path.GetFullPath(directory);
        var workDirectory = GetDocumentWorkDirectory();
        foreach (var journalPath in Directory.EnumerateFiles(
                     workDirectory, "alquitel_publish_*.journal"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var journal = ReadPublicationJournal(journalPath);
                if (!string.Equals(
                        journal.DirectoryPath,
                        normalizedDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!IsSafeJournalFileName(journal.DocumentFileName, ".docx") ||
                    !IsSafeJournalFileName(journal.PdfFileName, ".pdf") ||
                    journal.PdfSha256.Length != 64)
                    throw new InvalidDataException("Journal documental inválido.");

                var documentPath = Path.Combine(directory, journal.DocumentFileName);
                var pdfPath = Path.Combine(directory, journal.PdfFileName);
                if (!File.Exists(documentPath) && File.Exists(pdfPath))
                {
                    if (!DeleteFileByVerifiedHandle(pdfPath, journal.PdfSha256))
                        throw new InvalidDataException("El PDF ya no coincide con la publicación interrumpida.");
                }
                TryDelete(journalPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Warning(
                    "No se pudo recuperar una publicación documental interrumpida ({ErrorType})",
                    ex.GetType().Name);
                QuarantinePublicationJournal(journalPath);
            }
        }
    }

    private static PublicationJournal ReadPublicationJournal(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var header = "ALQJP1\n"u8;
        if (bytes.Length <= header.Length || !bytes.AsSpan(0, header.Length).SequenceEqual(header))
            throw new InvalidDataException("Journal documental inválido.");
        var plaintext = DpapiProtector.Unprotect(bytes[header.Length..]);
        return JsonSerializer.Deserialize<PublicationJournal>(plaintext)
            ?? throw new InvalidDataException("Journal documental vacío.");
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool DeleteFileByVerifiedHandle(string path, string expectedSha256)
    {
        using var handle = CreateFile(
            path,
            GenericRead | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        using var nonOwningHandle = new SafeFileHandle(
            handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(
            nonOwningHandle, FileAccess.Read, bufferSize: 81920, isAsync: false);
        var currentHash = SHA256.HashData(stream);
        var expectedHash = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(currentHash, expectedHash))
            return false;

        ConditionalDeleteTestHook?.Invoke(path);
        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return true;
    }

    private static void QuarantinePublicationJournal(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var quarantine = Path.Combine(GetDocumentWorkDirectory(), "Quarantine");
            Directory.CreateDirectory(quarantine);
            var token = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)).AsSpan(0, 8));
            File.Move(
                path,
                Path.Combine(quarantine, $"publication_{token}_{DateTime.UtcNow:yyyyMMddHHmmss}.bad"),
                overwrite: false);
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "No se pudo poner en cuarentena un journal documental ({ErrorType})",
                ex.GetType().Name);
        }
    }

    private static bool IsSafeJournalFileName(string value, string extension) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) &&
        value.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static void SweepStaleStagingFiles(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            var staleCandidates = Directory.EnumerateFiles(directory, ".alquitel-*.tmp.*");
            foreach (var path in staleCandidates)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    File.Delete(path);
                }
                catch
                {
                    // Un archivo activo o sin permisos se conserva y se reintentará después.
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "No se pudieron limpiar temporales documentales antiguos ({ErrorType})",
                ex.GetType().Name);
        }
    }

    private static void SweepStalePrivateSnapshots(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var pattern in new[]
                     {
                         "alquitel_template_*.docx", "alquitel_image_*.*", "alquitel_publish_*.journal.tmp",
                     })
            {
                foreach (var path in Directory.EnumerateFiles(directory, pattern))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                    }
                    catch
                    {
                        // Un archivo activo se conserva y se reintentará después.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("No se pudieron limpiar snapshots documentales antiguos ({ErrorType})", ex.GetType().Name);
        }
    }
}

internal sealed record PublishedDocument(string DocumentPath, string? PdfPath);

internal sealed class DocumentImageSnapshot : IDisposable
{
    public DocumentImageSnapshot(string path) => Path = path;
    public string Path { get; }
    public void Dispose() => DocumentGenerationSafety.TryDelete(Path);
}

internal sealed record PublicationJournal(
    string DirectoryPath,
    string DocumentFileName,
    string PdfFileName,
    string PdfSha256);
