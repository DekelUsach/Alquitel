using System;
using System.Linq;
using Alquitel.Core.Entities;

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

            ReplaceText(doc, "(fecha actual)", order.CreatedDate.ToString("dd/MM/yyyy"));
            ReplaceText(doc, "(fecha)",        order.EventDate?.ToString("dd/MM/yyyy") ?? order.CreatedDate.ToString("dd/MM/yyyy"));
            ReplaceText(doc, "[FECHA]",        order.CreatedDate.ToString("dd/MM/yyyy"));
            ReplaceText(doc, "{{FECHA}}",      order.CreatedDate.ToString("dd/MM/yyyy"));

            ReplaceText(doc, "(nro presupuesto)", order.BudgetNumber);
            ReplaceText(doc, "[NUMERO]",       order.BudgetNumber);
            ReplaceText(doc, "{{NUMERO}}",     order.BudgetNumber);
            ReplaceText(doc, "[PRESUPUESTO]",  order.BudgetNumber);

            ReplaceText(doc, "(nombre cliente)", order.Client?.CompanyName ?? "N/A");
            ReplaceText(doc, "(servicio contratado)", TagParser.StripTags(order.Items.FirstOrDefault()?.DescriptionSnapshot ?? order.Items.FirstOrDefault()?.Product?.Description) ?? "N/A");
            ReplaceText(doc, "(lugar del evento)", order.Location?.Name ?? "N/A");

            ReplaceText(doc, "(Empleado que hizo el presupuesto)", order.AdminName);
            ReplaceText(doc, "(empleado que hizo el presupuesto)", order.AdminName);
            ReplaceText(doc, "{{ADMIN}}",      order.AdminName);
            ReplaceText(doc, "[ADMIN]",        order.AdminName);

            ReplaceBookmark(doc, "BK_CLIENT_NAME",  order.Client?.CompanyName ?? "N/A");
            ReplaceBookmark(doc, "BK_CUIT",         order.Client?.Cuit ?? "N/A");
            ReplaceBookmark(doc, "BK_LOCATION",     order.Location?.Name ?? "N/A");
            ReplaceBookmark(doc, "BK_DATE",         order.CreatedDate.ToString("dd/MM/yyyy"));
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
