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

        /// <summary>
        /// Mata el WINWORD lanzado por la última sesión COM (si sigue vivo). Se usa solo
        /// en el camino de timeout: el hilo STA quedó bloqueado dentro de Word y nunca va
        /// a llegar al Dispose de la sesión.
        /// </summary>
        private static void KillOrphanWinword()
        {
            if (WordComSession.LastLaunchedPid is not int pid) return;
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                if (!proc.HasExited && proc.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
                {
                    proc.Kill();
                    AppLog.Warning("WINWORD colgado (PID {Pid}) terminado por timeout de generación", pid);
                }
            }
            catch (ArgumentException) { /* ya salió */ }
            catch (Exception ex) { AppLog.Warning(ex, "KillOrphanWinword failed for PID {Pid}", pid); }
        }

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

                        if (isTechnical)
                        {
                            // La OT lleva el título de sección "PRODUCCION" subrayado;
                            // la plantilla lo trae como texto plano.
                            PlaceholderReplacer.UnderlineOccurrences(session.Document, "PRODUCCION");
                            PlaceholderReplacer.UnderlineOccurrences(session.Document, "PRODUCCIÓN");
                        }

                        var searchRange = session.Document.Content;
                        if (searchRange.Find.Execute("{{PRODUCTOS_AQUI}}"))
                        {
                            searchRange.Text = "";

                            if (isTechnical)
                            {
                                // OT técnica: listado plano en Arial negrita, sin imágenes,
                                // precios ni tablas (formato del OT de referencia).
                                WorkOrderProductRenderer.RenderProducts(session.Document, searchRange, order.Items);
                            }
                            else
                            {
                                RenderBudgetProducts(session, searchRange, order, isTechnical);
                            }
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

                // Sin timeout, un diálogo modal imprevisto de Word (recuperación de
                // documento, activación) dejaba el hilo STA bloqueado y este await
                // colgado para siempre con un WINWORD invisible.
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(3)));
                if (completed != tcs.Task)
                {
                    KillOrphanWinword();
                    throw new TimeoutException(
                        "Word no respondió en 3 minutos y se abortó la generación del documento. " +
                        "Cerrá cualquier ventana de Word abierta y reintentá.");
                }
                await tcs.Task;
            });
        }

        /// <summary>
        /// Renderizado clásico de presupuesto/OF: título Montserrat, imagen flotante,
        /// campos dinámicos y tabla de costos por producto.
        /// </summary>
        private static void RenderBudgetProducts(WordComSession session, dynamic searchRange, Order order, bool isTechnical)
        {
            dynamic doc = session.Document!;

            // Guarda de fin de zona: el párrafo siguiente al tag ancla la
            // línea divisoria celeste. Un bookmark lo marca para que el
            // renderer nunca inserte productos dentro/después de él.
            try
            {
                int guardPos = (int)searchRange.Paragraphs[1].Range.End;
                doc.Bookmarks.Add(ProductRenderer.EndGuardBookmark, doc.Range(guardPos, guardPos));
            }
            catch (Exception ex) { AppLog.Warning(ex, "Could not place product end-guard bookmark"); }

            foreach (var item in order.Items)
            {
                ProductRenderer.RenderProduct(doc, session.WordApp, ref searchRange, item, isTechnical);
            }

            try
            {
                if (doc.Bookmarks.Exists(ProductRenderer.EndGuardBookmark))
                    doc.Bookmarks(ProductRenderer.EndGuardBookmark).Delete();
            }
            catch (Exception ex) { AppLog.Warning(ex, "Could not remove product end-guard bookmark"); }
        }
    }
}
