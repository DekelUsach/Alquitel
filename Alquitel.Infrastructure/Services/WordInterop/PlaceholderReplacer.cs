using System;
using System.Linq;
using Alquitel.Core.Entities;
using Alquitel.Core.Parsing;

namespace Alquitel.Infrastructure.Services.WordInterop
{
    public static class PlaceholderReplacer
    {
        public static void ReplaceAll(dynamic doc, Order order, bool isTechnical)
        {
            ReplaceText(doc, "[CLIENTE]",      order.Client?.CompanyName ?? "N/A");
            ReplaceText(doc, "{{CLIENTE}}",    order.Client?.CompanyName ?? "N/A");
            ReplaceText(doc, "<<CLIENTE>>",    order.Client?.CompanyName ?? "N/A");

            ReplaceText(doc, "[CUIT]",         order.Client?.Cuit ?? "N/A");
            ReplaceText(doc, "{{CUIT}}",       order.Client?.Cuit ?? "N/A");

            ReplaceText(doc, "[LUGAR]",        order.Location?.Name ?? "N/A");
            ReplaceText(doc, "{{LUGAR}}",      order.Location?.Name ?? "N/A");

            // CreatedDate is stored in UTC — render it in local time or documents
            // generated at night carry tomorrow's date (Argentina is UTC-3).
            string createdLocal = order.CreatedDate.ToLocalTime().ToString("dd/MM/yyyy");
            // La fecha del evento va en palabras y admite lapso:
            // "del 14 de abril al 15 de mayo de 2026".
            string eventDateWords = order.EventDate.HasValue
                ? Alquitel.Core.Helpers.SpanishDateFormatter.ToWordsRange(order.EventDate.Value, order.EventEndDate)
                : "CONSULTAR";
            ReplaceText(doc, "(fecha actual)", createdLocal);
            ReplaceText(doc, "(fecha)",        order.EventDate.HasValue ? eventDateWords : createdLocal);
            ReplaceText(doc, "[FECHA]",        createdLocal);
            ReplaceText(doc, "{{FECHA}}",      createdLocal);

            ReplaceText(doc, "(nro presupuesto)", order.BudgetNumber);
            ReplaceText(doc, "[NUMERO]",       order.BudgetNumber);
            ReplaceText(doc, "{{NUMERO}}",     order.BudgetNumber);
            ReplaceText(doc, "[PRESUPUESTO]",  order.BudgetNumber);

            ReplaceText(doc, "(nombre cliente)", order.Client?.CompanyName ?? "N/A");
            // Frase institucional fija: el encabezado del presupuesto siempre dice
            // "...solicitado por, alquiler y servicio de equipamiento audiovisual...".
            ReplaceText(doc, "(servicio contratado)", "alquiler y servicio de equipamiento audiovisual");
            ReplaceText(doc, "(lugar del evento)", order.Location?.Name ?? "N/A");

            ReplaceText(doc, "(Empleado que hizo el presupuesto)", order.AdminName);
            ReplaceText(doc, "(empleado que hizo el presupuesto)", order.AdminName);
            ReplaceText(doc, "{{ADMIN}}",      order.AdminName);
            ReplaceText(doc, "[ADMIN]",        order.AdminName);
            ReplaceText(doc, "{{USUARIO}}",    order.AdminName);

            // Placeholders de la plantilla de OT (orden de trabajo técnica).
            ReplaceText(doc, "(FECHA_EVENTO)",   eventDateWords);
            ReplaceText(doc, "{{FECHA_EVENTO}}", eventDateWords);

            // Contacto del cliente: "Nombre email teléfono" (solo los datos cargados).
            string contacto = string.Join("  ", new[] { order.Client?.ContactName, order.Client?.Email, order.Client?.Phone }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            ReplaceText(doc, "{{CONTACTO}}", contacto);

            ReplaceText(doc, "{{COMENTARIOS}}", order.Comments ?? string.Empty);
            ReplaceText(doc, "{{DIRECCION}}",   string.Empty);

            // ── Motor comercial: totales con descuento e IVA ──────────
            // Solo tienen efecto si la plantilla incluye los tags; una plantilla vieja
            // sin ellos sigue imprimiendo el total por producto como siempre.
            ReplaceText(doc, "{{SUBTOTAL}}", order.Total.ToString("C"));
            ReplaceText(doc, "{{DESCUENTO}}", order.DiscountValue > 0
                ? $"-{order.DiscountValue.ToString("C")}"
                : string.Empty);
            ReplaceText(doc, "{{IVA}}", order.AddVat ? order.VatValue.ToString("C") : string.Empty);
            ReplaceText(doc, "{{TOTAL}}", order.GrandTotal.ToString("C"));
            ReplaceText(doc, "{{TOTAL_FINAL}}", order.GrandTotal.ToString("C"));

            ReplaceBookmark(doc, "BK_CLIENT_NAME",  order.Client?.CompanyName ?? "N/A");
            ReplaceBookmark(doc, "BK_CUIT",         order.Client?.Cuit ?? "N/A");
            ReplaceBookmark(doc, "BK_LOCATION",     order.Location?.Name ?? "N/A");
            ReplaceBookmark(doc, "BK_DATE",         createdLocal);
            ReplaceBookmark(doc, "BK_BUDGET_NUM",   order.BudgetNumber);

            string descriptionProducts = string.Join("\n", order.Items.Select(i =>
                $"- {i.Quantity}x {TagParser.StripTags(i.DescriptionSnapshot ?? i.Product?.Description) ?? "Equipamiento"} | Subtotal: {i.Total:C}"));
            ReplaceText(doc, "(productos elegidos con sus descripciones, y el valor total)", descriptionProducts);

            if (doc.Bookmarks.Exists("BK_EQUIPMENT_TABLE"))
            {
                var bk = doc.Bookmarks("BK_EQUIPMENT_TABLE");
                var table = bk.Range.Tables[1];

                foreach (var item in order.Items)
                {
                    var row = table.Rows.Add();
                    row.Cells[1].Range.Text = TagParser.StripTags(item.DescriptionSnapshot ?? item.Product?.Description) ?? "Unknown";
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
        }

        /// <summary>
        /// Subraya todas las ocurrencias de un texto literal en el documento (ej. el
        /// título "PRODUCCION" de la orden de trabajo, que la plantilla trae sin formato).
        /// </summary>
        public static void UnderlineOccurrences(dynamic doc, string findText)
        {
            foreach (dynamic storyRange in doc.StoryRanges)
            {
                dynamic? currentRange = storyRange;
                while (currentRange != null)
                {
                    try
                    {
                        dynamic search = currentRange.Duplicate;
                        search.Find.ClearFormatting();
                        // Solo el título en MAYÚSCULAS y como palabra completa: sin esto
                        // también se subrayaría "producción" dentro de notas técnicas.
                        search.Find.MatchCase = true;
                        search.Find.MatchWholeWord = true;
                        while ((bool)search.Find.Execute(findText))
                        {
                            search.Font.Underline = 1; // wdUnderlineSingle
                            search.Collapse(0);        // wdCollapseEnd — seguir después del hallazgo
                        }
                    }
                    catch (Exception ex) { AppLog.Warning(ex, "Underline failed for text {Text}", findText); }

                    try { currentRange = currentRange.NextStoryRange; }
                    catch { currentRange = null; }
                }
            }
        }

        private static void ReplaceBookmark(dynamic doc, string name, string text)
        {
            if (!doc.Bookmarks.Exists(name)) return;

            var bk = doc.Bookmarks(name);
            var range = bk.Range;
            range.Text = text ?? string.Empty;
            doc.Bookmarks.Add(name, range);
        }

        private static void ReplaceText(dynamic doc, string findText, string replaceText)
        {
            string safeReplace = replaceText ?? string.Empty;

            foreach (dynamic storyRange in doc.StoryRanges)
            {
                dynamic? currentRange = storyRange;
                while (currentRange != null)
                {
                    try
                    {
                        if (safeReplace.Length <= 250)
                        {
                            currentRange.Find.ClearFormatting();
                            currentRange.Find.Execute(
                                findText, Type.Missing, Type.Missing, Type.Missing, Type.Missing,
                                Type.Missing, Type.Missing, 1 /*wdFindContinue*/, Type.Missing,
                                safeReplace, 2 /*wdReplaceAll*/);
                        }
                        else
                        {
                            // Word limita ReplaceWith a 255 caracteres y lanza COMException
                            // con textos largos (ej. la lista completa de productos). Para
                            // esos casos se localiza cada ocurrencia y se asigna Range.Text.
                            dynamic search = currentRange.Duplicate;
                            search.Find.ClearFormatting();
                            while ((bool)search.Find.Execute(findText))
                            {
                                search.Text = safeReplace;
                                search.Collapse(0); // wdCollapseEnd — seguir buscando después del reemplazo
                            }
                        }
                    }
                    catch (Exception ex) { AppLog.Warning(ex, "Find/Replace failed for placeholder {Placeholder}", findText); }

                    try { currentRange = currentRange.NextStoryRange; }
                    catch { currentRange = null; }
                }
            }
        }
    }
}
