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
        private static readonly IAsyncPolicy _retryPolicy = Policy
            .Handle<Exception>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    AppLog.Warning(exception, $"Error generating document (Retry {retryCount})");
                });

        public async Task GenerateDocumentAsync(Order order, string templatePath, string outputPath, bool isTechnical)
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

                        PlaceholderReplacer.ReplaceAll(session.Document, order, isTechnical);

                        var searchRange = session.Document.Content;
                        if (searchRange.Find.Execute("{{PRODUCTOS_AQUI}}"))
                        {
                            searchRange.Text = "";
                            foreach (var item in order.Items)
                            {
                                ProductRenderer.RenderProduct(session.Document, session.WordApp, ref searchRange, item, isTechnical);
                            }
                        }

                        session.SaveAndClose(outputPath);
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
