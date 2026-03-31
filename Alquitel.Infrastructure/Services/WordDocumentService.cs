using Alquitel.Core.Entities;
using Alquitel.Core.Interfaces;
using Polly;
using System.Runtime.InteropServices;
using Task = System.Threading.Tasks.Task;

namespace Alquitel.Infrastructure.Services
{
    public class WordDocumentService : IDocumentService
    {
        /// <summary>
        /// Exponential backoff for OneDrive file sync locks (IOException / COMException).
        /// </summary>
        private static readonly IAsyncPolicy _retryPolicy = Policy
            .Handle<COMException>()
            .Or<IOException>()
            .WaitAndRetryAsync(5, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        public async Task GenerateDocumentAsync(Order order, string templatePath, string outputPath, bool isTechnical)
        {
            await _retryPolicy.ExecuteAsync(() => Task.Run(() =>
            {
                dynamic? wordApp = null;
                dynamic? doc = null;

                try
                {
                    Type? wordType = Type.GetTypeFromProgID("Word.Application");
                    if (wordType == null)
                        throw new Exception("Microsoft Word no está instalado o no está registrado correctamente en este sistema. (ProgID 'Word.Application' no encontrado).");

                    wordApp = Activator.CreateInstance(wordType);
                    if (wordApp == null)
                        throw new Exception("No se pudo iniciar la instancia de Microsoft Word.");

                    wordApp.Visible = false;

                    // Open the template — doc must be assigned BEFORE any doc.* usage
                    doc = wordApp.Documents.Open(templatePath);

                    // Replace plain text placeholders (any format used in the template)
                    ReplaceText(doc, "[CLIENTE]",      order.Client?.CompanyName ?? "N/A");
                    ReplaceText(doc, "{{CLIENTE}}",    order.Client?.CompanyName ?? "N/A");
                    ReplaceText(doc, "<<CLIENTE>>",    order.Client?.CompanyName ?? "N/A");

                    ReplaceText(doc, "[CUIT]",         order.Client?.Cuit ?? "N/A");
                    ReplaceText(doc, "{{CUIT}}",       order.Client?.Cuit ?? "N/A");

                    ReplaceText(doc, "[LUGAR]",        order.Location?.Name ?? "N/A");
                    ReplaceText(doc, "{{LUGAR}}",      order.Location?.Name ?? "N/A");

                    ReplaceText(doc, "[FECHA]",        order.CreatedDate.ToString("dd/MM/yyyy"));
                    ReplaceText(doc, "{{FECHA}}",      order.CreatedDate.ToString("dd/MM/yyyy"));

                    ReplaceText(doc, "[NUMERO]",       order.BudgetNumber);
                    ReplaceText(doc, "{{NUMERO}}",     order.BudgetNumber);
                    ReplaceText(doc, "[PRESUPUESTO]",  order.BudgetNumber);
                    ReplaceText(doc, "[OT]",           order.BudgetNumber);

                    // Bookmark-based replacement (fallback for templates using Word Bookmarks)
                    ReplaceBookmark(doc, "BK_CLIENT_NAME",  order.Client?.CompanyName ?? "N/A");
                    ReplaceBookmark(doc, "BK_CUIT",         order.Client?.Cuit ?? "N/A");
                    ReplaceBookmark(doc, "BK_LOCATION",     order.Location?.Name ?? "N/A");
                    ReplaceBookmark(doc, "BK_DATE",         order.CreatedDate.ToString("dd/MM/yyyy"));
                    ReplaceBookmark(doc, "BK_BUDGET_NUM",   order.BudgetNumber);

                    // Fill equipment table if bookmark exists
                    if (doc.Bookmarks.Exists("BK_EQUIPMENT_TABLE"))
                    {
                        var bk = doc.Bookmarks("BK_EQUIPMENT_TABLE");
                        var table = bk.Range.Tables[1];

                        foreach (var item in order.Items)
                        {
                            var row = table.Rows.Add();

                            row.Cells[1].Range.Text = item.Product?.Description ?? "Unknown";
                            row.Cells[2].Range.Text = item.Quantity.ToString();

                            if (!isTechnical)
                            {
                                // OF / Presupuesto: include pricing
                                row.Cells[3].Range.Text = item.UnitPrice.ToString("C");
                                row.Cells[4].Range.Text = (item.Quantity * item.UnitPrice).ToString("C");
                            }
                            else
                            {
                                // OT: technical notes only, no monetary values
                                row.Cells[3].Range.Text = item.TechnicalNotes ?? string.Empty;
                            }
                        }
                    }

                    // Save output
                    doc.SaveAs2(outputPath);
                }
                finally
                {
                    if (doc != null)
                    {
                        try { doc.Close(false); } catch { }
                        Marshal.ReleaseComObject(doc);
                    }
                    if (wordApp != null)
                    {
                        try { wordApp.Quit(); } catch { }
                        Marshal.ReleaseComObject(wordApp);
                    }
                }
            }));
        }

        private static void ReplaceBookmark(dynamic doc, string name, string text)
        {
            if (!doc.Bookmarks.Exists(name)) return;

            var bk = doc.Bookmarks(name);
            var range = bk.Range;
            range.Text = text;

            // Re-register bookmark so its name survives text replacement
            doc.Bookmarks.Add(name, range);
        }

        private static void ReplaceText(dynamic doc, string findText, string replaceText)
        {
            var range = doc.Content;
            range.Find.ClearFormatting();
            // parameters for Execute: FindText, MatchCase, MatchWholeWord, MatchWildcards, MatchSoundsLike, MatchAllWordForms, Forward, Wrap, Format, ReplaceWith, Replace, MatchKashida...
            // the 11th argument is 'Replace' where 2 is wdReplaceAll.
            range.Find.Execute(findText, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, 1 /*wdFindContinue*/, Type.Missing, replaceText, 2 /*wdReplaceAll*/);
        }
    }
}
