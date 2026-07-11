using System.Collections.Generic;
using System.Globalization;
using Alquitel.Core.Entities;
using Alquitel.Core.Parsing;

namespace Alquitel.Infrastructure.Services.WordInterop
{
    /// <summary>
    /// Renderizado de {{PRODUCTOS_AQUI}} para la Orden de Trabajo técnica.
    /// Calcado del OT de referencia (OT 9054 - FERIA DEL LIBRO 2026): una línea por
    /// producto en Arial 14pt negrita y MAYÚSCULAS ("1 PANTALLA DE LED P2.9 DE 2.5 X 1.5"),
    /// con un párrafo en blanco entre productos. Sin imágenes, sin precios, sin tablas.
    /// </summary>
    public static class WorkOrderProductRenderer
    {
        private const string FONT_NAME = "Arial";
        private static readonly CultureInfo EsAr = new("es-AR");

        public static void RenderProducts(dynamic doc, dynamic insertRange, IEnumerable<OrderItem> items)
        {
            var range = insertRange;
            range.Collapse(0); // wdCollapseEnd

            foreach (var item in items)
            {
                // Párrafo en blanco antes de cada producto (también del primero, que en el
                // documento de referencia queda separado del título "PRODUCCION").
                range.InsertParagraphAfter();
                range.Collapse(0);

                string desc = TagParser.StripTags(item.DescriptionSnapshot ?? item.Product?.Description) ?? "Equipamiento";
                string line = $"{item.Quantity} {desc}";
                if (!string.IsNullOrWhiteSpace(item.RequestedMeasure))
                    line += $" DE {item.RequestedMeasure}";

                AppendParagraph(doc, range, line.ToUpper(EsAr), bold: true);

                // Las notas técnicas son el corazón de la OT (cableado, logística):
                // van debajo del producto en el mismo cuerpo pero sin negrita.
                if (!string.IsNullOrWhiteSpace(item.TechnicalNotes))
                    AppendParagraph(doc, range, item.TechnicalNotes.Trim(), bold: false);
            }
        }

        private static void AppendParagraph(dynamic doc, dynamic range, string text, bool bold)
        {
            try { range.set_Style(doc.Styles["Normal"]); } catch { }
            range.ParagraphFormat.LeftIndent = 0;
            range.ParagraphFormat.FirstLineIndent = 0;
            range.ParagraphFormat.RightIndent = 9.6f; // 0.34 cm, como las líneas de equipos del OT modelo
            range.ParagraphFormat.Alignment = 0;      // wdAlignParagraphLeft
            range.ParagraphFormat.SpaceBefore = 0;
            range.ParagraphFormat.SpaceAfter = 0;

            int start = (int)range.End;
            range.Collapse(0);
            range.InsertAfter(text);
            range.SetRange(start, range.End);
            range.Font.Name = FONT_NAME;
            range.Font.Size = 14; // Normal de la plantilla: Arial 14pt
            range.Font.Bold = bold ? 1 : 0;
            range.Font.Italic = 0;
            range.Font.Underline = 0;
            range.Font.Color = TagParserInterop.WD_BLACK;
            range.Collapse(0);
            range.InsertParagraphAfter();
            range.Collapse(0);
        }
    }
}
