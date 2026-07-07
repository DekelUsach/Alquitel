using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Polly;
using Alquitel.Infrastructure.Services.WordInterop;

namespace Alquitel.Infrastructure.Services
{
    public class WordDocumentService : IDocumentService
    {
        private static bool IsFileInUseException(Exception ex)
        {
            if (ex is IOException ioEx)
            {
                int hResult = ioEx.HResult & 0xFFFF;
                return hResult == 32 || hResult == 33;
            }
            if (ex is System.Runtime.InteropServices.COMException comEx)
            {
                // 0x800A1066: Command failed (often because file is in use)
                // 0x800A175D: Cannot open the document because it's locked by another user
                return comEx.ErrorCode == unchecked((int)0x800A1066) || 
                       comEx.ErrorCode == unchecked((int)0x800A175D);
            }
            return false;
        }

        private static readonly IAsyncPolicy _retryPolicy = Policy
            .Handle<Exception>(IsFileInUseException)
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    AppLog.Warning(exception, $"Error generating document (Retry {retryCount} due to file lock)");
                });

        public async Task GenerateDocumentAsync(Order order, string templatePath, string outputPath, bool isTechnical, bool exportPdf = false)
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var staThread = new Thread(() =>
                {
                    try
                    {
                        using var session = new WordComSession();
                        session.Initialize();
                        session.OpenTemplate(templatePath);

                        if (session.Document == null)
                            throw new InvalidOperationException("Word no pudo abrir la plantilla (documento nulo).");

                        PlaceholderReplacer.ReplaceAll(session.Document, order, isTechnical);

                        var searchRange = session.Document.Content;
                        if (searchRange.Find.Execute("{{PRODUCTOS_AQUI}}"))
                        {
                            searchRange.Text = "";

                            // Guarda de fin de zona: el párrafo siguiente al tag ancla la
                            // línea divisoria celeste. Un bookmark lo marca para que el
                            // renderer nunca inserte productos dentro/después de él.
                            try
                            {
                                int guardPos = (int)searchRange.Paragraphs[1].Range.End;
                                session.Document.Bookmarks.Add(ProductRenderer.EndGuardBookmark,
                                    session.Document.Range(guardPos, guardPos));
                            }
                            catch (Exception ex) { AppLog.Warning(ex, "Could not place product end-guard bookmark"); }

                            foreach (var item in order.Items)
                            {
                                ProductRenderer.RenderProduct(session.Document, session.WordApp, ref searchRange, item, isTechnical);
                            }

                            try
                            {
                                if (session.Document.Bookmarks.Exists(ProductRenderer.EndGuardBookmark))
                                    session.Document.Bookmarks(ProductRenderer.EndGuardBookmark).Delete();
                            }
                            catch (Exception ex) { AppLog.Warning(ex, "Could not remove product end-guard bookmark"); }
                        }

                        session.SaveAndClose(outputPath);
                        if (exportPdf)
                        {
                            var pdfPath = Path.ChangeExtension(outputPath, ".pdf");
                            session.ExportAsPdf(pdfPath);
                        }
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error(ex, "Word Document Generation Failed");
                        tcs.SetException(ex);
                    }
                });

                staThread.SetApartmentState(ApartmentState.STA);
                staThread.IsBackground = true;
                staThread.Start();

                await tcs.Task;
            });
        }
    }
}
