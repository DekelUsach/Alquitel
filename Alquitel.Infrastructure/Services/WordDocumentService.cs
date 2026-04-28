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
                        ReplaceText(doc, "(servicio contratado)", StripTags(order.Items.FirstOrDefault()?.Product?.Description) ?? "N/A");
                        ReplaceText(doc, "(lugar del evento)", order.Location?.Name ?? "N/A");

                        ReplaceText(doc, "(Empleado que hizo el presupuesto)", order.AdminName);
                        ReplaceText(doc, "(empleado que hizo el presupuesto)", order.AdminName);
                        ReplaceText(doc, "{{ADMIN}}",      order.AdminName);
                        ReplaceText(doc, "[ADMIN]",        order.AdminName);

                        // ── DYNAMIC PRODUCT INJECTION ({{PRODUCTOS_AQUI}}) ──
                        var searchRange = doc.Content;
                        if (searchRange.Find.Execute("{{PRODUCTOS_AQUI}}"))
                        {
                            searchRange.Text = ""; // collapse placeholder

                            foreach (var item in order.Items)
                            {
                                RenderProduct(doc, wordApp, ref searchRange, item, isTechnical);
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

        // Word color values are BGR: 0x00BBGGRR
        private const int WD_WHITE   = 0x00FFFFFF;
        private const int WD_BLACK   = 0x00000000;
        private const int WD_AUTO    = -16777216; // wdColorAutomatic — adapts to Word theme (dark/light)
        private const int WD_RED     = 0x000000FF; // #FF0000
        private const int WD_GREEN   = 0x00006600; // #006600
        private const int WD_DARKRED = 0x000000C0; // #C00000
        private const int WD_BLUE    = 0x00C7681F; // #1F68C7
        private const string FONT_NAME = "Montserrat";

        private sealed class Segment
        {
            public string Text = "";
            public int Color = WD_BLACK;
            public bool Bold;
            public bool Italic;
            public bool Underline;
        }

        // Parse inline color/style tags. Supported: [red] [green] [darkred] [blue] [white] [black] [b] [i] [u]
        private static List<Segment> ParseSegments(string? text, int defaultColor, bool defaultBold = false, bool defaultUnderline = false)
        {
            var result = new List<Segment>();
            if (string.IsNullOrEmpty(text)) return result;

            int color = defaultColor;
            bool bold = defaultBold, italic = false, underline = defaultUnderline;
            var stack = new Stack<(int color, bool bold, bool italic, bool underline)>();

            int i = 0;
            var buf = new System.Text.StringBuilder();
            void Flush()
            {
                if (buf.Length == 0) return;
                result.Add(new Segment { Text = buf.ToString(), Color = color, Bold = bold, Italic = italic, Underline = underline });
                buf.Clear();
            }

            while (i < text.Length)
            {
                if (text[i] == '[')
                {
                    int close = text.IndexOf(']', i + 1);
                    if (close > i)
                    {
                        string tag = text.Substring(i + 1, close - i - 1).Trim().ToLowerInvariant();
                        bool isClose = tag.StartsWith("/");
                        string name = isClose ? tag.Substring(1) : tag;
                        int? newColor = name switch
                        {
                            "red"     => WD_RED,
                            "green"   => WD_GREEN,
                            "darkred" => WD_DARKRED,
                            "blue"    => WD_BLUE,
                            "white"   => WD_WHITE,
                            "black"   => WD_BLACK,
                            _ => (int?)null
                        };
                        bool isStyle = name == "b" || name == "i" || name == "u";

                        if (newColor.HasValue || isStyle)
                        {
                            Flush();
                            if (!isClose)
                            {
                                stack.Push((color, bold, italic, underline));
                                if (newColor.HasValue) color = newColor.Value;
                                if (name == "b") bold = true;
                                if (name == "i") italic = true;
                                if (name == "u") underline = true;
                            }
                            else if (stack.Count > 0)
                            {
                                var s = stack.Pop();
                                color = s.color; bold = s.bold; italic = s.italic; underline = s.underline;
                            }
                            i = close + 1;
                            continue;
                        }
                    }
                }
                buf.Append(text[i]);
                i++;
            }
            Flush();
            return result;
        }

        private static string? StripTags(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z]+\]", "");
        }

        private static int HexToBgr(string hex, int fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(hex) || !hex.StartsWith("#") || hex.Length != 7) return fallback;
                int r = Convert.ToInt32(hex.Substring(1, 2), 16);
                int g = Convert.ToInt32(hex.Substring(3, 2), 16);
                int b = Convert.ToInt32(hex.Substring(5, 2), 16);
                return r | (g << 8) | (b << 16);
            }
            catch { return fallback; }
        }

        // Append colored segments to a Range (range gets advanced via InsertAfter)
        private static void AppendSegments(dynamic range, IEnumerable<Segment> segments, int sizePt)
        {
            foreach (var s in segments)
            {
                if (string.IsNullOrEmpty(s.Text)) continue;
                int startLen = (int)range.End;
                range.Collapse(0); // wdCollapseEnd = 0
                range.InsertAfter(s.Text);
                range.SetRange(startLen, range.End);
                range.Font.Name = FONT_NAME;
                range.Font.Size = sizePt;
                range.Font.Bold = s.Bold ? 1 : 0;
                range.Font.Italic = s.Italic ? 1 : 0;
                range.Font.Underline = s.Underline ? 1 /*wdUnderlineSingle*/ : 0;
                range.Font.Color = s.Color;
                // Clear inherited highlight/shading from template hyperlink style
                try { range.HighlightColorIndex = 0; } catch { }
                try { range.Shading.BackgroundPatternColor = -16777216; /* wdColorAutomatic */ } catch { }
                try { range.Shading.Texture = 0; } catch { }
                try { range.Font.Underline = s.Underline ? 1 : 0; } catch { }
                range.Collapse(0);
            }
        }

        private static void ResetParagraphStyle(dynamic doc, dynamic range)
        {
            try { range.set_Style(doc.Styles["Normal"]); } catch { }
            try { range.ParagraphFormat.Shading.BackgroundPatternColor = -16777216; } catch { }
        }

        private static void RenderProduct(dynamic doc, dynamic wordApp, ref dynamic insertRange, OrderItem item, bool isTechnical)
        {
            // ── 1. TITLE PARAGRAPH ──
            insertRange.Collapse(0);
            ResetParagraphStyle(doc, insertRange);
            insertRange.ParagraphFormat.LeftIndent = wordApp.CentimetersToPoints(1.9f);
            insertRange.ParagraphFormat.SpaceBefore = 6;
            insertRange.ParagraphFormat.SpaceAfter = 0;

            var titleSegments = ParseSegments(item.Product?.Description ?? "Producto", WD_BLACK, defaultBold: true);
            AppendSegments(insertRange, titleSegments, 12);

            // Insert floating image anchored to the title paragraph (left, wrap tight)
            if (!string.IsNullOrEmpty(item.ImagePath) && System.IO.File.Exists(item.ImagePath))
            {
                try
                {
                    int titleStart = (int)insertRange.Paragraphs[1].Range.Start;
                    int titleEnd   = (int)insertRange.Paragraphs[1].Range.End;
                    var anchorRange = doc.Range(titleStart, titleStart);

                    dynamic shape = doc.Shapes.AddPicture(
                        FileName: item.ImagePath,
                        LinkToFile: false,
                        SaveWithDocument: true,
                        Left: 0f,
                        Top: 0f,
                        Width: wordApp.CentimetersToPoints(2.5f),
                        Height: wordApp.CentimetersToPoints(2.5f),
                        Anchor: anchorRange);

                    shape.WrapFormat.Type = 3; // wdWrapTight
                    shape.WrapFormat.Side = 2; // wdWrapRight (text on right)
                    shape.RelativeHorizontalPosition = 2; // wdRelativeHorizontalPositionColumn
                    shape.RelativeVerticalPosition   = 3; // wdRelativeVerticalPositionParagraph
                    shape.Left = wordApp.CentimetersToPoints(0f);
                    shape.Top  = wordApp.CentimetersToPoints(0f);
                }
                catch { /* image insert is best-effort */ }
            }

            // New paragraph after title
            insertRange.Collapse(0);
            insertRange.InsertParagraphAfter();
            insertRange.Collapse(0);

            // ── 2. DETAIL PARAGRAPHS (custom fields) ──
            if (!string.IsNullOrWhiteSpace(item.CustomFieldsJson))
            {
                try
                {
                    var fields = JsonSerializer.Deserialize<List<CustomFieldDefinition>>(item.CustomFieldsJson);
                    if (fields != null)
                    {
                        foreach (var f in fields)
                        {
                            ResetParagraphStyle(doc, insertRange);
                            insertRange.ParagraphFormat.LeftIndent = wordApp.CentimetersToPoints(1.9f);
                            insertRange.ParagraphFormat.SpaceBefore = 0;
                            insertRange.ParagraphFormat.SpaceAfter = 0;

                            int fieldColor = HexToBgr(f.ColorHex, WD_BLACK);

                            // Label run (bold/underline per field flags)
                            if (!string.IsNullOrEmpty(f.Label))
                            {
                                var labelSegs = new List<Segment>
                                {
                                    new Segment {
                                        Text = string.IsNullOrEmpty(f.Value) ? f.Label : f.Label + ": ",
                                        Color = fieldColor, Bold = f.IsBold, Underline = f.IsUnderline
                                    }
                                };
                                AppendSegments(insertRange, labelSegs, 9);
                            }

                            // Value run (parsed for inline tags, defaults to plain in same color)
                            if (!string.IsNullOrEmpty(f.Value))
                            {
                                var valueSegs = ParseSegments(f.Value, fieldColor);
                                AppendSegments(insertRange, valueSegs, 9);
                            }

                            insertRange.Collapse(0);
                            insertRange.InsertParagraphAfter();
                            insertRange.Collapse(0);
                        }
                    }
                }
                catch { }
            }

            // ── 3. REQUESTED MEASURE ──
            if (!string.IsNullOrWhiteSpace(item.RequestedMeasure))
            {
                ResetParagraphStyle(doc, insertRange);
                insertRange.ParagraphFormat.LeftIndent = wordApp.CentimetersToPoints(1.9f);
                insertRange.ParagraphFormat.SpaceBefore = 0;
                insertRange.ParagraphFormat.SpaceAfter = 0;

                AppendSegments(insertRange, new[] {
                    new Segment { Text = "Medida solicitada: ", Color = WD_BLACK, Bold = true, Underline = true }
                }, 9);
                AppendSegments(insertRange, ParseSegments(item.RequestedMeasure, WD_RED, defaultBold: true), 9);

                insertRange.Collapse(0);
                insertRange.InsertParagraphAfter();
                insertRange.Collapse(0);
            }

            // Empty spacer paragraph (so image float ends before summary)
            insertRange.ParagraphFormat.LeftIndent = 0;
            insertRange.InsertParagraphAfter();
            insertRange.Collapse(0);

            // ── 4. SUMMARY TABLE 1×4 ──
            var summaryTable = doc.Tables.Add(insertRange, 1, 4);
            summaryTable.AllowAutoFit = false;
            try
            {
                summaryTable.Rows.Alignment = 1; // wdAlignRowCenter
                summaryTable.Rows.HeightRule = 2; // wdRowHeightExact
                summaryTable.Rows.Height = wordApp.CentimetersToPoints(0.65f);
            }
            catch { }

            summaryTable.Cell(1, 1).Width = wordApp.CentimetersToPoints(2.2f);
            summaryTable.Cell(1, 2).Width = wordApp.CentimetersToPoints(2.0f);
            summaryTable.Cell(1, 3).Width = wordApp.CentimetersToPoints(4.0f);
            summaryTable.Cell(1, 4).Width = wordApp.CentimetersToPoints(5.0f);

            // White inner borders, no outer
            try
            {
                summaryTable.Borders.OutsideLineStyle = 0; // wdLineStyleNone
                summaryTable.Borders.InsideLineStyle  = 1; // wdLineStyleSingle
                summaryTable.Borders.InsideLineWidth  = 4; // 0.5pt
                summaryTable.Borders.InsideColor      = WD_WHITE;
            }
            catch { }

            string c1 = $"Cant.: {item.Quantity}";
            string c2 = $"Días: {item.Dias}";
            string c3 = isTechnical ? "Costo U.: -----" : $"Costo U.: {(item.UnitPrice == 0 ? "-----" : item.UnitPrice.ToString("N0", new System.Globalization.CultureInfo("es-AR")))}";
            string c4 = isTechnical ? "Total: -----" : $"Total: $ {item.Total.ToString("N0", new System.Globalization.CultureInfo("es-AR"))}";

            string[] cells = { c1, c2, c3, c4 };
            for (int c = 1; c <= 4; c++)
            {
                dynamic cell = summaryTable.Cell(1, c);
                cell.Shading.BackgroundPatternColor = WD_BLUE;
                cell.VerticalAlignment = 1; // wdCellAlignVerticalCenter
                var cr = cell.Range;
                cr.Text = cells[c - 1];
                cr.Font.Name = FONT_NAME;
                cr.Font.Size = 10;
                cr.Font.Bold = 1;
                cr.Font.Color = WD_WHITE;
                cr.ParagraphFormat.Alignment = c == 1 || c == 2 ? 1 /*wdAlignParagraphCenter*/ : 0 /*wdAlignParagraphLeft*/;
            }

            // Move insertRange after the summary table
            int after = (int)summaryTable.Range.End;
            insertRange = doc.Range(after, after);
            insertRange.InsertParagraphAfter();
            insertRange.InsertParagraphAfter();
            insertRange.Collapse(0);
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
