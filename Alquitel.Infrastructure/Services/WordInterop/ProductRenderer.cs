using System;
using System.Collections.Generic;
using System.Text.Json;
using Alquitel.Core.Entities;
using Alquitel.Core.Parsing;
using Alquitel.Infrastructure.Services;

namespace Alquitel.Infrastructure.Services.WordInterop
{
    public static class ProductRenderer
    {
        // Siempre Montserrat, como el presupuesto corporativo de referencia. Si la fuente
        // no está instalada en el puesto que genera, Word muestra un sustituto pero el
        // documento conserva "Montserrat" y se ve correcto en cualquier máquina que la tenga.
        private const string FONT_NAME = "Montserrat";

        /// <summary>
        /// Bookmark colocado por WordDocumentService al inicio del párrafo que sigue a
        /// {{PRODUCTOS_AQUI}} (el que ancla la línea divisoria celeste y el bloque
        /// "Incluye en todos los casos"). El renderizado nunca escribe dentro de ese
        /// párrafo: sin esta guarda, la línea terminaba flotando entre los productos.
        /// </summary>
        public const string EndGuardBookmark = "__ALQ_FIN_PRODUCTOS";

        public static void RenderProduct(
            dynamic doc,
            dynamic wordApp,
            ref dynamic insertRange,
            OrderItem item,
            bool isTechnical,
            ICollection<string>? warnings = null)
        {
            // Si el punto de inserción alcanzó (o pasó) el párrafo de cierre, crear un
            // párrafo nuevo ANTES de él y seguir ahí: el contenido del producto jamás
            // debe entrar al párrafo que ancla la línea divisoria.
            try
            {
                if (doc.Bookmarks.Exists(EndGuardBookmark))
                {
                    int guardPos = (int)doc.Bookmarks(EndGuardBookmark).Range.Start;
                    if ((int)insertRange.End >= guardPos)
                    {
                        var guardRange = doc.Range(guardPos, guardPos);
                        guardRange.InsertParagraphBefore();
                        insertRange = doc.Range(guardRange.Start, guardRange.Start);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("Falló el reposicionamiento del límite ({ErrorType}, 0x{HResult:X8})", ex.GetType().Name, ex.HResult);
            }

            // ── 1. TITLE PARAGRAPH ──
            insertRange.Collapse(0);
            ResetParagraphStyle(doc, insertRange);
            insertRange.ParagraphFormat.LeftIndent = wordApp.CentimetersToPoints(1.9f);
            insertRange.ParagraphFormat.FirstLineIndent = 0;
            insertRange.ParagraphFormat.RightIndent = 0;
            insertRange.ParagraphFormat.Alignment = 0; // wdAlignParagraphLeft
            insertRange.ParagraphFormat.SpaceBefore = 6;
            insertRange.ParagraphFormat.SpaceAfter = 0;

            var titleSegments = TagParser.Parse(item.DescriptionSnapshot ?? item.Product?.Description ?? "Producto", "#000000", defaultBold: true);
            AppendSegments(insertRange, titleSegments, 12);

            // Insert floating image anchored to the title paragraph (left, wrap tight)
            if (!string.IsNullOrEmpty(item.ImagePath))
            {
                if (!DocumentGenerationSafety.TryCreateImageSnapshot(
                        item.ImagePath, out var imageSnapshot, out var imageWarning))
                {
                    if (!string.IsNullOrEmpty(imageWarning)) warnings?.Add(imageWarning);
                }
                else
                {
                    using (imageSnapshot)
                    {
                    dynamic? anchorRange = null;
                    dynamic? shape = null;
                    try
                    {
                        int titleStart = (int)insertRange.Paragraphs[1].Range.Start;
                        anchorRange = doc.Range(titleStart, titleStart);

                        // El presupuesto de referencia usa miniaturas de ~1.6 cm ancladas detrás
                        // del texto, en el margen que deja la sangría de 1.9 cm del título.
                        shape = doc.Shapes.AddPicture(
                            FileName: imageSnapshot!.Path,
                            LinkToFile: false,
                            SaveWithDocument: true,
                            Left: 0f,
                            Top: 0f,
                            Width: wordApp.CentimetersToPoints(1.6f),
                            Height: wordApp.CentimetersToPoints(1.6f),
                            Anchor: anchorRange);

                        try { shape.WrapFormat.Type = 5; /* wdWrapBehind */ }
                        catch { shape.WrapFormat.Type = 3; /* wdWrapNone */ }
                        try { shape.ZOrder(5); /* msoSendBehindText */ } catch { }
                        shape.RelativeHorizontalPosition = 2;
                        shape.RelativeVerticalPosition = 3;
                        shape.Left = wordApp.CentimetersToPoints(0f);
                        shape.Top = wordApp.CentimetersToPoints(0f);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning(
                            "No se pudo insertar una imagen de producto en Word ({ErrorType})",
                            ex.GetType().Name);
                        warnings?.Add("Se omitió una imagen de producto que no pudo insertarse.");
                    }
                    finally
                    {
                        ReleaseCom(shape);
                        ReleaseCom(anchorRange);
                    }
                    }
                }
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
                            ApplyDetailParagraphFormat(wordApp, insertRange);

                            string fieldColorHex = string.IsNullOrEmpty(f.ColorHex) ? "#000000" : f.ColorHex;

                            if (!string.IsNullOrEmpty(f.Label))
                            {
                                var labelSegs = new List<TextSegment>
                                {
                                    new TextSegment {
                                        Text = string.IsNullOrEmpty(f.Value) ? f.Label : f.Label + ": ",
                                        ColorHex = fieldColorHex, Bold = f.IsBold, Underline = f.IsUnderline
                                    }
                                };
                                AppendSegments(insertRange, labelSegs, 9);
                            }

                            if (!string.IsNullOrEmpty(f.Value))
                            {
                                var valueSegs = TagParser.Parse(f.Value, fieldColorHex);
                                AppendSegments(insertRange, valueSegs, 9);
                            }

                            insertRange.Collapse(0);
                            insertRange.InsertParagraphAfter();
                            insertRange.Collapse(0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning(
                        "Falló el render de campos del item {ItemId} ({ErrorType}, 0x{HResult:X8})",
                        item.Id, ex.GetType().Name, ex.HResult);
                }
            }

            // ── 3. REQUESTED MEASURE ──
            if (!string.IsNullOrWhiteSpace(item.RequestedMeasure))
            {
                ResetParagraphStyle(doc, insertRange);
                ApplyDetailParagraphFormat(wordApp, insertRange);

                AppendSegments(insertRange, new[] {
                    new TextSegment { Text = "Medida solicitada: ", ColorHex = "#000000", Bold = true, Underline = true }
                }, 9);
                AppendSegments(insertRange, TagParser.Parse(item.RequestedMeasure, "#FF0000", defaultBold: true), 9);

                insertRange.Collapse(0);
                insertRange.InsertParagraphAfter();
                insertRange.Collapse(0);
            }

            insertRange.ParagraphFormat.LeftIndent = 0;
            insertRange.ParagraphFormat.FirstLineIndent = 0;
            insertRange.ParagraphFormat.RightIndent = 0;
            insertRange.ParagraphFormat.Alignment = 0; // wdAlignParagraphLeft
            insertRange.InsertParagraphAfter();
            insertRange.Collapse(0);

            // ── 4. SUMMARY TABLE 1×4 ──
            // Geometría calcada del presupuesto de referencia: 12 cm de ancho total
            // (2.0/1.75/3.44/4.81) con sangría izquierda de 5.25 cm desde el margen.
            var summaryTable = doc.Tables.Add(insertRange, 1, 4);
            summaryTable.AllowAutoFit = false;
            try
            {
                summaryTable.Rows.Alignment = 0; // wdAlignRowLeft
                summaryTable.Rows.SetLeftIndent(wordApp.CentimetersToPoints(5.25f), 0 /*wdAdjustNone*/);
                summaryTable.Rows.HeightRule = 2; // wdRowHeightExact
                summaryTable.Rows.Height = wordApp.CentimetersToPoints(0.63f);
            }
            catch { }

            summaryTable.Cell(1, 1).Width = wordApp.CentimetersToPoints(2.0f);
            summaryTable.Cell(1, 2).Width = wordApp.CentimetersToPoints(1.75f);
            summaryTable.Cell(1, 3).Width = wordApp.CentimetersToPoints(3.44f);
            summaryTable.Cell(1, 4).Width = wordApp.CentimetersToPoints(4.81f);

            try
            {
                summaryTable.Borders.OutsideLineStyle = 0; // wdLineStyleNone
                summaryTable.Borders.InsideLineStyle  = 1; // wdLineStyleSingle
                summaryTable.Borders.InsideLineWidth  = 4; // 0.5pt
                summaryTable.Borders.InsideColor      = TagParserInterop.WD_WHITE;
            }
            catch { }

            string c1 = $"Cant.:  {item.Quantity}";
            string c2 = $"Días:  {item.Dias}";
            string c3 = isTechnical ? "Costo U.:     -----" : $"Costo U.:  {(item.UnitPrice == 0 ? "   -----" : Alquitel.Core.Helpers.MoneyFormatter.WholeNumber(item.UnitPrice))}";
            string c4 = isTechnical ? "Total: -----" : $"Total: $   {Alquitel.Core.Helpers.MoneyFormatter.WholeNumber(item.Total)}";

            string[] cells = { c1, c2, c3, c4 };
            for (int c = 1; c <= 4; c++)
            {
                dynamic cell = summaryTable.Cell(1, c);
                cell.Shading.BackgroundPatternColor = TagParserInterop.WD_BLUE;
                cell.VerticalAlignment = 1; // wdCellAlignVerticalCenter
                var cr = cell.Range;
                cr.Text = cells[c - 1];
                cr.Font.Name = FONT_NAME;
                cr.Font.Size = c == 4 ? 12 : 10; // el "Total" va en 12pt como en el modelo
                cr.Font.Bold = 1;
                cr.Font.Color = TagParserInterop.WD_WHITE;
                cr.ParagraphFormat.Alignment = 0; // wdAlignParagraphLeft
            }

            int after = (int)summaryTable.Range.End;
            int guardStart = int.MaxValue;
            try
            {
                if (doc.Bookmarks.Exists(EndGuardBookmark))
                    guardStart = (int)doc.Bookmarks(EndGuardBookmark).Range.Start;
            }
            catch { }

            if (after >= guardStart)
            {
                // La tabla quedó pegada al párrafo de cierre: no insertar separadores acá
                // (entrarían al párrafo que ancla la línea divisoria). El próximo producto
                // crea su propio párrafo antes del cierre vía la guarda inicial.
                insertRange = doc.Range(guardStart, guardStart);
            }
            else
            {
                insertRange = doc.Range(after, after);
                insertRange.InsertParagraphAfter();
                insertRange.InsertParagraphAfter();
                insertRange.Collapse(0);
            }
        }

        private static void ReleaseCom(object? value)
        {
            try
            {
                if (value != null && System.Runtime.InteropServices.Marshal.IsComObject(value))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(value);
            }
            catch
            {
                // El cierre de la sesión completa es la última red de seguridad.
            }
        }

        private static void AppendSegments(dynamic range, IEnumerable<TextSegment> segments, int sizePt)
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
                range.Font.Color = TagParserInterop.HexToBgr(s.ColorHex, TagParserInterop.WD_BLACK);
                try { range.HighlightColorIndex = 0; } catch { }
                try { range.Shading.BackgroundPatternColor = TagParserInterop.WD_AUTO; } catch { }
                try { range.Shading.Texture = 0; } catch { }
                try { range.Font.Underline = s.Underline ? 1 : 0; } catch { }
                range.Collapse(0);
            }
        }

        /// <summary>
        /// Formato de párrafo de las líneas de especificaciones, calcado del presupuesto
        /// de referencia: sangría izquierda 0.66 cm + primera línea 1.25 cm (arranca a
        /// 1.9 cm, las líneas envueltas vuelven a 0.66), sangría derecha 0.81 cm,
        /// justificado.
        /// </summary>
        private static void ApplyDetailParagraphFormat(dynamic wordApp, dynamic range)
        {
            range.ParagraphFormat.LeftIndent = wordApp.CentimetersToPoints(0.66f);
            range.ParagraphFormat.FirstLineIndent = wordApp.CentimetersToPoints(1.25f);
            range.ParagraphFormat.RightIndent = wordApp.CentimetersToPoints(0.81f);
            range.ParagraphFormat.Alignment = 3; // wdAlignParagraphJustify
            range.ParagraphFormat.SpaceBefore = 0;
            range.ParagraphFormat.SpaceAfter = 0;
        }

        private static void ResetParagraphStyle(dynamic doc, dynamic range)
        {
            try { range.set_Style(doc.Styles["Normal"]); } catch { }
            try { range.ParagraphFormat.Shading.BackgroundPatternColor = TagParserInterop.WD_AUTO; } catch { }
        }
    }
}
