using System.Text.Json;
using System.Threading;
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
            await _retryPolicy.ExecuteAsync(async () =>
            {
                // Word.Application is an STA COM server. Task.Run uses MTA thread-pool threads,
                // which forces COM cross-apartment marshaling and causes hangs because Word
                // internally pumps Windows messages that MTA threads cannot process.
                // The fix: run all COM work on a dedicated STA thread.
                var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
                    System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

                var staThread = new Thread(() =>
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
                        wordApp.DisplayAlerts = 0;       // wdAlertsNone — suprime diálogos invisibles
                        wordApp.AutomationSecurity = 3;  // msoAutomationSecurityForceDisable — sin macros

                        // Kill stale Word lock file (~$filename.docx) left by crashed instances
                        string templateDir  = Path.GetDirectoryName(templatePath)!;
                        string templateFile = Path.GetFileName(templatePath);
                        string lockFile     = Path.Combine(templateDir, "~$" + templateFile);
                        try { if (File.Exists(lockFile)) File.Delete(lockFile); } catch { }

                        // Copy template to a temp file to avoid read-only/OneDrive lock dialogs
                        string tempPath = Path.Combine(Path.GetTempPath(), $"alquitel_tmp_{Guid.NewGuid():N}.docx");
                        File.Copy(templatePath, tempPath, overwrite: true);

                        // Clear read-only attribute if present
                        var attrs = File.GetAttributes(tempPath);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(tempPath, attrs & ~FileAttributes.ReadOnly);

                        try
                        {
                        // Disable Protected View so Word doesn't open the copy in read-only sandbox
                        try
                        {
                            wordApp.Options.DisableHardwareGraphicsAcceleration = true; // stability
                            // Turn off all Protected View gates
                            dynamic pvOptions = wordApp.Options.ProtectedViewOptions;
                            pvOptions.OpenUnsafeLocationsInProtectedView  = false;
                            pvOptions.OpenFilesFromInternetInProtectedView = false;
                            pvOptions.OpenFilesInUnsafeLocationsInProtectedView = false;
                        }
                        catch { /* Options not available on all Word versions — ignore */ }

                        // Open the copy — no lock dialogs, original untouched
                        doc = wordApp.Documents.Open(tempPath, ReadOnly: false, AddToRecentFiles: false,
                                                     ConfirmConversions: false);

                        // If Word still opened in Protected View, promote to editable document
                        if (wordApp.ProtectedViewWindows.Count > 0)
                        {
                            try
                            {
                                dynamic pvw = wordApp.ProtectedViewWindows[1];
                                doc = pvw.Edit();
                            }
                            catch { /* already a regular document */ }
                        }

                        // Remove document-level editing protection (form protection, etc.)
                        try
                        {
                            if ((int)doc.ProtectionType != -1) // -1 = wdNoProtection
                                doc.Unprotect(Password: "");
                        }
                        catch { /* password-protected — skip */ }

                        // Replace plain text placeholders (any format used in the template)
                        ReplaceText(doc, "[CLIENTE]",      order.Client?.CompanyName ?? "N/A");
                        ReplaceText(doc, "{{CLIENTE}}",    order.Client?.CompanyName ?? "N/A");
                        ReplaceText(doc, "<<CLIENTE>>",    order.Client?.CompanyName ?? "N/A");

                        ReplaceText(doc, "[CUIT]",         order.Client?.Cuit ?? "N/A");
                        ReplaceText(doc, "{{CUIT}}",       order.Client?.Cuit ?? "N/A");

                        ReplaceText(doc, "[LUGAR]",        order.Location?.Name ?? "N/A");
                        ReplaceText(doc, "{{LUGAR}}",      order.Location?.Name ?? "N/A");

                        ReplaceText(doc, "(fecha actual)", order.CreatedDate.ToString("dd/MM/yyyy"));
                        ReplaceText(doc, "(fecha)",        order.EventDate?.ToString("dd/MM/yyyy") ?? order.CreatedDate.ToString("dd/MM/yyyy"));
                        ReplaceText(doc, "[FECHA]",        order.CreatedDate.ToString("dd/MM/yyyy"));
                        ReplaceText(doc, "{{FECHA}}",      order.CreatedDate.ToString("dd/MM/yyyy"));

                        ReplaceText(doc, "(nro presupuesto)", order.BudgetNumber);
                        ReplaceText(doc, "[NUMERO]",       order.BudgetNumber);
                        ReplaceText(doc, "{{NUMERO}}",     order.BudgetNumber);
                        ReplaceText(doc, "[PRESUPUESTO]",  order.BudgetNumber);

                        ReplaceText(doc, "(nombre cliente)", order.Client?.CompanyName ?? "N/A");
                        ReplaceText(doc, "(servicio contratado)", order.Items.FirstOrDefault()?.Product?.Description ?? "N/A");
                        ReplaceText(doc, "(lugar del evento)", order.Location?.Name ?? "N/A");

                        ReplaceText(doc, "(Empleado que hizo el presupuesto)", order.AdminName);
                        ReplaceText(doc, "(empleado que hizo el presupuesto)", order.AdminName);
                        ReplaceText(doc, "{{ADMIN}}",      order.AdminName);
                        ReplaceText(doc, "[ADMIN]",        order.AdminName);

                        // ── DYNAMIC PRODUCT INJECTION ({{PRODUCTOS_AQUI}}) ──
                        var searchRange = doc.Content;
                        if (searchRange.Find.Execute("{{PRODUCTOS_AQUI}}"))
                        {
                            searchRange.Text = ""; // clear placeholder

                            foreach (var item in order.Items)
                            {
                                // 1. Create Layout Table (2 columns: left Image, right Details)
                                var prodTable = doc.Tables.Add(searchRange, 1, 2);
                                prodTable.Borders.Enable = 0;
                                prodTable.Cell(1, 1).Width = wordApp.CentimetersToPoints(4f);
                                prodTable.Cell(1, 2).Width = wordApp.CentimetersToPoints(12f);

                                // Insert Image if available
                                if (!string.IsNullOrEmpty(item.ImagePath) && System.IO.File.Exists(item.ImagePath))
                                {
                                    object missing = System.Reflection.Missing.Value;
                                    var imageRange = prodTable.Cell(1, 1).Range;
                                    imageRange.Collapse(ref missing);
                                    var shape = imageRange.InlineShapes.AddPicture(item.ImagePath, false, true);
                                    shape.Width = 100;
                                    shape.Height = 100;
                                }

                                var textRange = prodTable.Cell(1, 2).Range;
                                textRange.Text = "";

                                // Insert Title
                                var titlePara = textRange.Paragraphs.Add();
                                titlePara.Range.Text = item.Product?.Description + "\n";
                                titlePara.Range.Font.Bold = 1;
                                titlePara.Range.Font.Size = 14;
                                titlePara.Range.Font.Color = 16777215; // WdColorWhite

                                // Insert Custom Fields
                                if (!string.IsNullOrWhiteSpace(item.CustomFieldsJson))
                                {
                                    try
                                    {
                                        var fields = JsonSerializer.Deserialize<List<CustomFieldDefinition>>(item.CustomFieldsJson);
                                        if (fields != null)
                                        {
                                            foreach (var f in fields)
                                            {
                                                var p = textRange.Paragraphs.Add();
                                                p.Range.Font.Bold = f.IsBold ? 1 : 0;
                                                p.Range.Font.Underline = f.IsUnderline ? 1 : 0;
                                                p.Range.Font.Size = 11;

                                                try
                                                {
                                                    if (f.ColorHex.StartsWith("#") && f.ColorHex.Length == 7)
                                                    {
                                                        string r = f.ColorHex.Substring(1, 2);
                                                        string g = f.ColorHex.Substring(3, 2);
                                                        string b = f.ColorHex.Substring(5, 2);
                                                        p.Range.Font.Color = Convert.ToInt32(r, 16) + (Convert.ToInt32(g, 16) * 0x100) + (Convert.ToInt32(b, 16) * 0x10000);
                                                    }
                                                    else
                                                    {
                                                        p.Range.Font.Color = 16777215;
                                                    }
                                                }
                                                catch { p.Range.Font.Color = 16777215; }

                                                p.Range.Text = $"{f.Label}: {f.Value}\n";
                                            }
                                        }
                                    }
                                    catch { }
                                }

                                // Insert Requested Measure
                                if (!string.IsNullOrWhiteSpace(item.RequestedMeasure))
                                {
                                    var measureP = textRange.Paragraphs.Add();
                                    measureP.Range.Text = $"Medida solicitada: {item.RequestedMeasure}\n";
                                    measureP.Range.Font.Bold = 1;
                                    measureP.Range.Font.Underline = 1;
                                    measureP.Range.Font.Size = 11;
                                    measureP.Range.Font.Color = 16777215;
                                }

                                // Get position exactly after the product table
                                int nextPos = prodTable.Range.End;
                                var summaryRange = doc.Range(nextPos, nextPos);
                                summaryRange.InsertParagraphAfter(); // A little spacing
                                nextPos = summaryRange.End;
                                summaryRange = doc.Range(nextPos, nextPos);

                                // 2. Create Summary Table (Cant, Dias, Costo U, Total)
                                var summaryTable = doc.Tables.Add(summaryRange, 1, 4);
                                summaryTable.Borders.Enable = 1;
                                summaryTable.Borders.InsideLineStyle = 1;
                                summaryTable.Borders.OutsideLineStyle = 1;

                                summaryTable.Shading.BackgroundPatternColor = 16757640;
                                summaryTable.Cell(1, 1).Range.Text = $"Cant.: {item.Quantity}";
                                summaryTable.Cell(1, 2).Range.Text = $"Días: {item.Dias}";
                                summaryTable.Cell(1, 3).Range.Text = isTechnical ? "Costo U.: ----" : $"Costo U.: {item.UnitPrice:C}";
                                summaryTable.Cell(1, 4).Range.Text = isTechnical ? "Total: ----" : $"Total: {item.Total:C}";

                                foreach (dynamic cell in summaryTable.Range.Cells)
                                {
                                    cell.Range.Font.Bold = 1;
                                    cell.Range.Font.Color = 0;
                                    cell.VerticalAlignment = 1;
                                }

                                // Position for the next product
                                int finalPos = summaryTable.Range.End;
                                searchRange = doc.Range(finalPos, finalPos);
                                searchRange.InsertParagraphAfter();
                                searchRange.InsertParagraphAfter();
                                object collapseEnd = 0;
                                searchRange.Collapse(ref collapseEnd);
                            }
                        }

                        // Bookmark-based replacement
                        ReplaceBookmark(doc, "BK_CLIENT_NAME",  order.Client?.CompanyName ?? "N/A");
                        ReplaceBookmark(doc, "BK_CUIT",         order.Client?.Cuit ?? "N/A");
                        ReplaceBookmark(doc, "BK_LOCATION",     order.Location?.Name ?? "N/A");
                        ReplaceBookmark(doc, "BK_DATE",         order.CreatedDate.ToString("dd/MM/yyyy"));
                        ReplaceBookmark(doc, "BK_BUDGET_NUM",   order.BudgetNumber);

                        string descriptionProducts = string.Join("\n", order.Items.Select(i =>
                            $"- {i.Quantity}x {i.Product?.Description ?? "Equipamiento"} | Subtotal: {i.Total:C}"));
                        ReplaceText(doc, "(productos elegidos con sus descripciones, y el valor total)", descriptionProducts);

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
                                    row.Cells[3].Range.Text = item.UnitPrice.ToString("C");
                                    row.Cells[4].Range.Text = item.Total.ToString("C");
                                }
                                else
                                {
                                    row.Cells[3].Range.Text = item.TechnicalNotes ?? string.Empty;
                                }
                            }
                        }

                        // Save output
                        doc.SaveAs2(outputPath);

                        tcs.SetResult(true);
                        }
                        finally
                        {
                            // Delete temp copy regardless of success or failure
                            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
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
                });

                staThread.SetApartmentState(ApartmentState.STA);
                staThread.IsBackground = true;
                staThread.Start();

                await tcs.Task;
            });
        }

        private static void ReplaceBookmark(dynamic doc, string name, string text)
        {
            if (!doc.Bookmarks.Exists(name)) return;

            var bk = doc.Bookmarks(name);
            var range = bk.Range;
            range.Text = text ?? string.Empty;

            // Re-register bookmark so its name survives text replacement
            doc.Bookmarks.Add(name, range);
        }

        private static void ReplaceText(dynamic doc, string findText, string replaceText)
        {
            string safeReplace = replaceText ?? string.Empty;

            foreach (dynamic storyRange in doc.StoryRanges)
            {
                dynamic currentRange = storyRange;
                while (currentRange != null)
                {
                    try
                    {
                        currentRange.Find.ClearFormatting();
                        currentRange.Find.Execute(
                            findText, Type.Missing, Type.Missing, Type.Missing, Type.Missing,
                            Type.Missing, Type.Missing, 1 /*wdFindContinue*/, Type.Missing,
                            safeReplace, 2 /*wdReplaceAll*/);
                    }
                    catch { }

                    try { currentRange = currentRange.NextStoryRange; }
                    catch { currentRange = null; }
                }
            }
        }
    }
}
