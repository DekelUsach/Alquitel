using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Alquitel.Infrastructure.Services.WordInterop;
using Polly;

namespace Alquitel.Infrastructure.Services;

public interface IWordDocumentComposer
{
    void Compose(
        IWordComSession session,
        Order order,
        bool isTechnical,
        ICollection<string> warnings,
        IProgress<DocumentGenerationProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class WordDocumentService : IDocumentService
{
    private static readonly SemaphoreSlim GenerationGate = new(1, 1);
    private readonly IWordComSessionFactory _sessionFactory;
    private readonly IWordDocumentComposer _composer;
    private readonly TimeSpan _timeout;

    private static bool IsFileInUseException(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            int hResult = ioEx.HResult & 0xFFFF;
            return hResult == 32 || hResult == 33;
        }
        if (ex is COMException comEx)
        {
            return comEx.ErrorCode == unchecked((int)0x800A1066) ||
                   comEx.ErrorCode == unchecked((int)0x800A175D);
        }
        return false;
    }

    private static readonly IAsyncPolicy<DocumentGenerationResult> RetryPolicy = Policy<DocumentGenerationResult>
        .Handle<Exception>(IsFileInUseException)
        .WaitAndRetryAsync(
            3,
            retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
            onRetry: (outcome, _, retryCount, _) =>
                AppLog.Warning(
                    "Documento bloqueado; reintento {RetryCount} ({ErrorType}, 0x{HResult:X8})",
                    retryCount,
                    outcome.Exception?.GetType().Name ?? "Unknown",
                    outcome.Exception?.HResult ?? 0));

    public WordDocumentService()
        : this(new WordComSessionFactory(), new WordDocumentComposer(), TimeSpan.FromMinutes(3))
    {
    }

    public WordDocumentService(
        IWordComSessionFactory sessionFactory,
        IWordDocumentComposer composer,
        TimeSpan? timeout = null)
    {
        _sessionFactory = sessionFactory;
        _composer = composer;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<DocumentGenerationResult> GenerateDocumentAsync(
        Order order,
        string templatePath,
        string outputPath,
        bool isTechnical,
        bool exportPdf = false,
        IProgress<DocumentGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        progress?.Report(new DocumentGenerationProgress(
            DocumentGenerationStage.Validating, 5, "Validando plantilla"));
        var request = await Task.Run(
            () => DocumentGenerationSafety.Validate(
                templatePath, outputPath, allowLegacyProductBookmark: true, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var releaseImmediately = true;
        var gateAcquired = false;
        try
        {
            await GenerationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            return await RetryPolicy.ExecuteAsync(
                ct => RunStaAsync(order, request, isTechnical, exportPdf, progress, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (WordWorkerStillRunningException ex)
        {
            releaseImmediately = false;
            _ = ReleaseWhenWorkerStopsAsync(ex.CleanupTask, request.TemplatePath);
            ExceptionDispatchInfo.Capture(ex.UserException).Throw();
            throw;
        }
        finally
        {
            if (releaseImmediately)
            {
                DocumentGenerationSafety.TryDelete(request.TemplatePath);
                if (gateAcquired) GenerationGate.Release();
            }
        }
    }

    private async Task<DocumentGenerationResult> RunStaAsync(
        Order order,
        ValidatedDocumentRequest request,
        bool isTechnical,
        bool exportPdf,
        IProgress<DocumentGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operationToken = operationCts.Token;
        var tcs = new TaskCompletionSource<DocumentGenerationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stagedDocument = DocumentGenerationSafety.CreateStagingPath(
            request.RequestedOutputPath, ".docx");
        var stagedPdf = exportPdf
            ? DocumentGenerationSafety.CreateStagingPath(request.RequestedOutputPath, ".pdf")
            : null;
        var commitStarted = false;

        var staThread = new Thread(() =>
        {
            try
            {
                var warnings = request.Warnings.ToList();
                using (var session = _sessionFactory.Create())
                {
                    operationToken.ThrowIfCancellationRequested();
                    progress?.Report(new DocumentGenerationProgress(
                        DocumentGenerationStage.Preparing, 15, "Iniciando Word de forma segura"));
                    session.Initialize();
                    operationToken.ThrowIfCancellationRequested();
                    session.OpenTemplate(request.TemplatePath);
                    _composer.Compose(
                        session, order, isTechnical, warnings, progress, operationToken);

                    operationToken.ThrowIfCancellationRequested();
                    progress?.Report(new DocumentGenerationProgress(
                        DocumentGenerationStage.Saving, 82, "Guardando documento"));
                    session.SaveWorkingCopy(stagedDocument);
                    if (exportPdf)
                    {
                        operationToken.ThrowIfCancellationRequested();
                        progress?.Report(new DocumentGenerationProgress(
                            DocumentGenerationStage.ExportingPdf, 90, "Exportando PDF"));
                        session.ExportAsPdf(stagedPdf!);
                    }
                }

                operationToken.ThrowIfCancellationRequested();
                FlushFile(stagedDocument);
                if (stagedPdf != null) FlushFile(stagedPdf);
                var published = DocumentGenerationSafety.Publish(
                    stagedDocument,
                    stagedPdf,
                    request.RequestedOutputPath,
                    operationToken,
                    () => Volatile.Write(ref commitStarted, true));
                progress?.Report(new DocumentGenerationProgress(
                    DocumentGenerationStage.Completed, 100, "Documento generado"));
                tcs.TrySetResult(new DocumentGenerationResult(
                    published.DocumentPath, published.PdfPath, warnings.AsReadOnly()));
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                AppLog.Error(
                    "Falló la generación Word (plantilla {TemplateId}, {ErrorType}, 0x{HResult:X8})",
                    request.TemplateId,
                    ex.GetType().Name,
                    ex.HResult);
                tcs.TrySetException(ex);
            }
            finally
            {
                DocumentGenerationSafety.TryDelete(stagedDocument);
                DocumentGenerationSafety.TryDelete(stagedPdf);
                cleanupTcs.TrySetResult();
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Name = "Alquitel Word document generation";
        staThread.Start();

        try
        {
            return await tcs.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (Volatile.Read(ref commitStarted))
                return await tcs.Task.ConfigureAwait(false);
            operationCts.Cancel();
            var userError = new TimeoutException(
                "Word no respondió dentro del tiempo permitido. No se publicó ningún archivo; podés reintentar.");
            if (!await AwaitCleanupAsync(cleanupTcs.Task).ConfigureAwait(false))
                throw new WordWorkerStillRunningException(userError, cleanupTcs.Task);
            throw userError;
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref commitStarted))
                return await tcs.Task.ConfigureAwait(false);
            operationCts.Cancel();
            var userError = new OperationCanceledException(cancellationToken);
            if (!await AwaitCleanupAsync(cleanupTcs.Task).ConfigureAwait(false))
                throw new WordWorkerStillRunningException(userError, cleanupTcs.Task);
            throw userError;
        }
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<bool> AwaitCleanupAsync(Task workerTask)
    {
        try
        {
            await workerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ReleaseWhenWorkerStopsAsync(Task cleanupTask, string templateSnapshotPath)
    {
        try
        {
            await cleanupTask.ConfigureAwait(false);
        }
        finally
        {
            DocumentGenerationSafety.TryDelete(templateSnapshotPath);
            GenerationGate.Release();
        }
    }

    private sealed class WordWorkerStillRunningException(
        Exception userException,
        Task cleanupTask) : Exception("La sesión STA de Word continúa cerrándose.", userException)
    {
        public Exception UserException { get; } = userException;
        public Task CleanupTask { get; } = cleanupTask;
    }
}

public sealed class WordDocumentComposer : IWordDocumentComposer
{
    public void Compose(
        IWordComSession session,
        Order order,
        bool isTechnical,
        ICollection<string> warnings,
        IProgress<DocumentGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        dynamic? document = session.Document;
        if (document == null)
            throw new InvalidOperationException("Word no pudo abrir la plantilla.");

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new DocumentGenerationProgress(
            DocumentGenerationStage.ReplacingFields, 35, "Completando datos"));
        PlaceholderReplacer.ReplaceAll(document, order, isTechnical);

        if (isTechnical)
        {
            PlaceholderReplacer.UnderlineOccurrences(document, "PRODUCCION");
            PlaceholderReplacer.UnderlineOccurrences(document, "PRODUCCIÓN");
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new DocumentGenerationProgress(
            DocumentGenerationStage.RenderingProducts, 60, "Agregando productos"));
        dynamic? searchRange = null;
        try
        {
            searchRange = document.Content;
            if (!searchRange.Find.Execute("{{PRODUCTOS_AQUI}}")) return;
            searchRange.Text = "";

            if (isTechnical)
            {
                WorkOrderProductRenderer.RenderProducts(document, searchRange, order.Items);
                return;
            }

            RenderBudgetProducts(session, searchRange, order, warnings, cancellationToken);
        }
        finally
        {
            if (searchRange != null && Marshal.IsComObject(searchRange))
                Marshal.FinalReleaseComObject(searchRange);
        }
    }

    private static void RenderBudgetProducts(
        IWordComSession session,
        dynamic searchRange,
        Order order,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        dynamic document = session.Document!;
        try
        {
            int guardPos = (int)searchRange.Paragraphs[1].Range.End;
            document.Bookmarks.Add(
                ProductRenderer.EndGuardBookmark,
                document.Range(guardPos, guardPos));
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "No se pudo crear el límite de productos ({ErrorType}, 0x{HResult:X8})",
                ex.GetType().Name, ex.HResult);
        }

        foreach (var item in order.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProductRenderer.RenderProduct(
                document, session.WordApp, ref searchRange, item, false, warnings);
        }

        try
        {
            if (document.Bookmarks.Exists(ProductRenderer.EndGuardBookmark))
                document.Bookmarks(ProductRenderer.EndGuardBookmark).Delete();
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "No se pudo retirar el límite de productos ({ErrorType}, 0x{HResult:X8})",
                ex.GetType().Name, ex.HResult);
        }
    }
}
