using System;
using System.Collections.Generic;
using System.Text.Json;
using Alquitel.Core.Entities;

namespace Alquitel.Infrastructure.Services.WordInterop
{
    public static class ProductRenderer
    {
        private static readonly string FONT_NAME;

        static ProductRenderer()
        {
            FONT_NAME = "Calibri";
            try
            {
                using var fonts = new System.Drawing.Text.InstalledFontCollection();
                foreach (var f in fonts.Families)
                {
                    if (f.Name.Equals("Montserrat", StringComparison.OrdinalIgnoreCase))
                    {
                        FONT_NAME = "Montserrat";
                        break;
                    }
                }
            }
            catch { }
        }

        public static void RenderProduct(dynamic doc, dynamic wordApp, ref dynamic insertRange, OrderItem item, bool isTechnical)
        {
            // ── 1. TITLE PARAGRAPH ──
            insertRange.Collapse(0);
            ResetParagraphStyle(doc, insertRange);
            insertRange.ParagraphFormat.LeftIndent = wordApp.CentimetersToPoints(1.9f);
            insertRange.ParagraphFormat.SpaceBefore = 6;
            insertRange.ParagraphFormat.SpaceAfter = 0;

            var titleSegments = TagParser.ParseSegments(item.DescriptionSnapshot ?? item.Product?.Description ?? "Producto", TagParser.WD_BLACK, defaultBold: true);
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
                    shape.WrapFormat.Side = 2; // wdWrapRight
                    shape.RelativeHorizontalPosition = 2; // wdRelativeHorizontalPositionColumn
                    shape.RelativeVerticalPosition   = 3; // wdRelativeVerticalPositionParagraph
                    shape.Left = wordApp.CentimetersToPoints(0f);
                    shape.Top  = wordApp.CentimetersToPoints(0f);
                }
                catch { /* image insert is best-effort */ }
            }

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

                            int fieldColor = TagParser.HexToBgr(f.ColorHex, TagParser.WD_BLACK);

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

                            if (!string.IsNullOrEmpty(f.Value))
                            {
                                var valueSegs = TagParser.ParseSegments(f.Value, fieldColor);
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
                    new Segment { Text = "Medida solicitada: ", Color = TagParser.WD_BLACK, Bold = true, Underline = true }
                }, 9);
                AppendSegments(insertRange, TagParser.ParseSegments(item.RequestedMeasure, TagParser.WD_RED, defaultBold: true), 9);

                insertRange.Collapse(0);
                insertRange.InsertParagraphAfter();
                insertRange.Collapse(0);
            }

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

            try
            {
                summaryTable.Borders.OutsideLineStyle = 0; // wdLineStyleNone
                summaryTable.Borders.InsideLineStyle  = 1; // wdLineStyleSingle
                summaryTable.Borders.InsideLineWidth  = 4; // 0.5pt
                summaryTable.Borders.InsideColor      = TagParser.WD_WHITE;
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
                cell.Shading.BackgroundPatternColor = TagParser.WD_BLUE;
                cell.VerticalAlignment = 1; // wdCellAlignVerticalCenter
                var cr = cell.Range;
                cr.Text = cells[c - 1];
                cr.Font.Name = FONT_NAME;
                cr.Font.Size = 10;
                cr.Font.Bold = 1;
                cr.Font.Color = TagParser.WD_WHITE;
                cr.ParagraphFormat.Alignment = c == 1 || c == 2 ? 1 : 0;
            }

            int after = (int)summaryTable.Range.End;
            insertRange = doc.Range(after, after);
            insertRange.InsertParagraphAfter();
            insertRange.InsertParagraphAfter();
            insertRange.Collapse(0);
        }

        private static void AppendSegments(dynamic range, IEnumerable<Segment> segments, int sizePt)
        {
            foreach (var s in segments)
            {
                if (string.IsNullOrEmpty(s.Text)) continue;
                int startLen = (int)range.End;
                range.Collapse(0);
                range.InsertAfter(s.Text);
                range.SetRange(startLen, range.End);
                range.Font.Name = FONT_NAME;
                range.Font.Size = sizePt;
                range.Font.Bold = s.Bold ? 1 : 0;
                range.Font.Italic = s.Italic ? 1 : 0;
                range.Font.Underline = s.Underline ? 1 : 0;
                range.Font.Color = s.Color;
                try { range.HighlightColorIndex = 0; } catch { }
                try { range.Shading.BackgroundPatternColor = TagParser.WD_AUTO; } catch { }
                try { range.Shading.Texture = 0; } catch { }
                try { range.Font.Underline = s.Underline ? 1 : 0; } catch { }
                range.Collapse(0);
            }
        }

        private static void ResetParagraphStyle(dynamic doc, dynamic range)
        {
            try { range.set_Style(doc.Styles["Normal"]); } catch { }
            try { range.ParagraphFormat.Shading.BackgroundPatternColor = TagParser.WD_AUTO; } catch { }
        }
    }
}
