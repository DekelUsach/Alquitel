using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Core.Interfaces;
using Alquitel.Core.Parsing;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Motor documental EXPERIMENTAL sobre OpenXML SDK: genera .docx sin Word instalado
    /// (sin proceso WINWORD, sin STA threads, ~10× más rápido que COM). Se activa con el
    /// feature flag "Documents:Engine": "openxml" en appsettings; el default sigue siendo
    /// el motor COM (<see cref="WordDocumentService"/>).
    ///
    /// Limitaciones conocidas frente al motor COM (por eso el flag):
    /// - El render de productos es simplificado: título con estilos BBCode + specs +
    ///   tabla de costos básica; sin imagen flotante con ajuste estrecho.
    /// - No exporta PDF (ExportAsFixedFormat es de Word); si se pide, se loguea y se omite.
    /// - No procesa la tabla legada BK_EQUIPMENT_TABLE ni bookmarks BK_*.
    /// </summary>
    public class OpenXmlDocumentService : IDocumentService
    {
        public Task GenerateDocumentAsync(Order order, string templatePath, string outputPath, bool isTechnical, bool exportPdf = false)
        {
            return Task.Run(() =>
            {
                if (!File.Exists(templatePath))
                    throw new FileNotFoundException("No se encontró la plantilla.", templatePath);

                // Se trabaja sobre un temporal y se mueve al final: editar in-place sobre
                // outputPath dejaba un .docx corrupto en la carpeta del usuario (que
                // PresupuestosView cataloga como válido) si algo fallaba a mitad.
                string tempPath = Path.Combine(Path.GetTempPath(), $"alquitel_oxml_{Guid.NewGuid():N}.docx");
                try
                {
                    File.Copy(templatePath, tempPath, overwrite: true);

                    using (var doc = WordprocessingDocument.Open(tempPath, isEditable: true))
                    {
                        var body = doc.MainDocumentPart?.Document.Body
                            ?? throw new InvalidOperationException("La plantilla no tiene cuerpo de documento.");

                        ReplacePlaceholders(doc, order, isTechnical);
                        RenderProducts(doc, body, order, isTechnical);

                        doc.MainDocumentPart!.Document.Save();
                    }

                    File.Move(tempPath, outputPath, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch (Exception ex) { AppLog.Warning(ex, "No se pudo borrar el temporal {Temp}", tempPath); }
                }

                if (exportPdf)
                    AppLog.Warning("Motor OpenXML: exportación a PDF no soportada (requiere Word/COM) — se omitió para {Output}", outputPath);

                AppLog.Information("Documento generado con motor OpenXML: {Output}", outputPath);
            });
        }

        // ── Reemplazo de placeholders ────────────────────────────────

        private static void ReplacePlaceholders(WordprocessingDocument doc, Order order, bool isTechnical)
        {
            string createdLocal = order.CreatedDate.ToLocalTime().ToString("dd/MM/yyyy");
            string eventDateWords = order.EventDate.HasValue
                ? Alquitel.Core.Helpers.SpanishDateFormatter.ToWordsRange(order.EventDate.Value, order.EventEndDate)
                : "CONSULTAR";
            string contacto = string.Join("  ", new[] { order.Client?.ContactName, order.Client?.Email, order.Client?.Phone }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            var map = new Dictionary<string, string>
            {
                ["[CLIENTE]"] = order.Client?.CompanyName ?? "N/A",
                ["{{CLIENTE}}"] = order.Client?.CompanyName ?? "N/A",
                ["<<CLIENTE>>"] = order.Client?.CompanyName ?? "N/A",
                ["[CUIT]"] = order.Client?.Cuit ?? "N/A",
                ["{{CUIT}}"] = order.Client?.Cuit ?? "N/A",
                ["[LUGAR]"] = order.Location?.Name ?? "N/A",
                ["{{LUGAR}}"] = order.Location?.Name ?? "N/A",
                ["(fecha actual)"] = createdLocal,
                ["(fecha)"] = order.EventDate.HasValue ? eventDateWords : createdLocal,
                ["[FECHA]"] = createdLocal,
                ["{{FECHA}}"] = createdLocal,
                ["(nro presupuesto)"] = order.BudgetNumber,
                ["[NUMERO]"] = order.BudgetNumber,
                ["{{NUMERO}}"] = order.BudgetNumber,
                ["[PRESUPUESTO]"] = order.BudgetNumber,
                ["(nombre cliente)"] = order.Client?.CompanyName ?? "N/A",
                ["(servicio contratado)"] = "alquiler y servicio de equipamiento audiovisual",
                ["(lugar del evento)"] = order.Location?.Name ?? "N/A",
                ["(Empleado que hizo el presupuesto)"] = order.AdminName,
                ["(empleado que hizo el presupuesto)"] = order.AdminName,
                ["{{ADMIN}}"] = order.AdminName,
                ["[ADMIN]"] = order.AdminName,
                ["{{USUARIO}}"] = order.AdminName,
                ["(FECHA_EVENTO)"] = eventDateWords,
                ["{{FECHA_EVENTO}}"] = eventDateWords,
                ["{{CONTACTO}}"] = contacto,
                ["{{COMENTARIOS}}"] = order.Comments ?? string.Empty,
                ["{{DIRECCION}}"] = string.Empty,
                ["{{SUBTOTAL}}"] = MoneyFormatter.Currency(order.Total),
                ["{{DESCUENTO}}"] = order.DiscountValue > 0 ? $"-{MoneyFormatter.Currency(order.DiscountValue)}" : string.Empty,
                ["{{IVA}}"] = order.AddVat ? MoneyFormatter.Currency(order.VatValue) : string.Empty,
                ["{{TOTAL}}"] = MoneyFormatter.Currency(order.GrandTotal),
                ["{{TOTAL_FINAL}}"] = MoneyFormatter.Currency(order.GrandTotal),
            };

            foreach (var part in EnumerateTextParts(doc))
                ReplaceInElement(part, map);
        }

        private static IEnumerable<OpenXmlElement> EnumerateTextParts(WordprocessingDocument doc)
        {
            var main = doc.MainDocumentPart!;
            if (main.Document.Body != null) yield return main.Document.Body;
            foreach (var hp in main.HeaderParts)
                if (hp.Header != null) yield return hp.Header;
            foreach (var fp in main.FooterParts)
                if (fp.Footer != null) yield return fp.Footer;
        }

        /// <summary>
        /// Reemplaza placeholders aunque Word los haya partido en varios runs: por cada
        /// párrafo se concatena el texto completo y, si contiene algún placeholder, se
        /// vuelca el texto resultante al primer run (conservando su formato) y se vacían
        /// los demás.
        /// </summary>
        private static void ReplaceInElement(OpenXmlElement root, Dictionary<string, string> map)
        {
            foreach (var paragraph in root.Descendants<W.Paragraph>())
            {
                var texts = paragraph.Descendants<W.Text>().ToList();
                if (texts.Count == 0) continue;

                string full = string.Concat(texts.Select(t => t.Text));
                if (!map.Keys.Any(k => full.Contains(k))) continue;

                foreach (var (key, value) in map)
                    full = full.Replace(key, value);

                SetRunText(texts[0], full);
                for (int i = 1; i < texts.Count; i++)
                    texts[i].Text = string.Empty;
            }
        }

        /// <summary>
        /// Vuelca texto (posiblemente multilínea) al run del W.Text dado. Un '\n' dentro
        /// de un W.Text no genera salto en Word: los valores multilínea (ej. Comments
        /// en {{COMENTARIOS}}) necesitan elementos W.Break entre líneas.
        /// </summary>
        private static void SetRunText(W.Text target, string value)
        {
            value = value.Replace("\r\n", "\n");
            if (!value.Contains('\n'))
            {
                target.Text = value;
                target.Space = SpaceProcessingModeValues.Preserve;
                return;
            }

            if (target.Parent is not W.Run run)
            {
                target.Text = value.Replace('\n', ' ');
                target.Space = SpaceProcessingModeValues.Preserve;
                return;
            }

            var props = run.RunProperties?.CloneNode(true);
            run.RemoveAllChildren();
            if (props != null) run.AppendChild(props);

            var lines = value.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) run.AppendChild(new W.Break());
                run.AppendChild(new W.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
        }

        // ── Render de productos ({{PRODUCTOS_AQUI}}) ─────────────────

        private static void RenderProducts(WordprocessingDocument doc, W.Body body, Order order, bool isTechnical)
        {
            var marker = body.Descendants<W.Paragraph>()
                .FirstOrDefault(p => string.Concat(p.Descendants<W.Text>().Select(t => t.Text))
                    .Contains("{{PRODUCTOS_AQUI}}"));
            if (marker == null) return;

            uint drawingId = 9000; // IDs únicos para los DocProperties de las imágenes
            var cursor = (OpenXmlElement)marker;
            foreach (var item in order.Items)
            {
                // Título con estilos BBCode del snapshot
                var title = new W.Paragraph(new W.ParagraphProperties(
                    new W.Indentation { Left = "1077" }, // 1.9 cm, mismo margen que el motor COM
                    new W.SpacingBetweenLines { After = "120", Before = "160" }));
                foreach (var seg in TagParser.Parse(item.DescriptionSnapshot ?? item.Product?.Description, defaultBold: true))
                {
                    if (string.IsNullOrEmpty(seg.Text)) continue;
                    string text = title.ChildElements.OfType<W.Run>().Any()
                        ? seg.Text
                        : $"{item.Quantity} x {seg.Text}";
                    // El título del producto siempre va en negrita (mismo criterio que el motor COM).
                    title.AppendChild(MakeRun(text,
                        bold: true, italic: seg.Italic, underline: seg.Underline,
                        colorHex: seg.ColorHex, size: 24));
                }

                // Miniatura flotante anclada al título (detrás del texto, en el margen que
                // deja la sangría de 1.9 cm), paridad con Shapes.AddPicture del motor COM.
                if (!string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath))
                {
                    try
                    {
                        title.AppendChild(BuildFloatingImageRun(doc.MainDocumentPart!, item.ImagePath, ++drawingId));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning(ex, "Best-effort image insert (OpenXML) failed for {ImagePath}", item.ImagePath);
                    }
                }
                cursor = InsertAfter(cursor, title);

                // Campos técnicos / specs
                foreach (var field in DeserializeFields(item.CustomFieldsJson))
                {
                    var spec = new W.Paragraph(new W.ParagraphProperties(
                        new W.Indentation { Left = "560" },
                        new W.SpacingBetweenLines { After = "40" }));
                    string label = string.IsNullOrWhiteSpace(field.Label) ? "" : $"{field.Label}: ";
                    foreach (var seg in TagParser.Parse(label + field.Value, field.ColorHex ?? "#000000", field.IsBold, field.IsUnderline))
                    {
                        if (string.IsNullOrEmpty(seg.Text)) continue;
                        spec.AppendChild(MakeRun(seg.Text, seg.Bold, seg.Italic, seg.Underline, seg.ColorHex, 20));
                    }
                    cursor = InsertAfter(cursor, spec);
                }

                // Medida solicitada
                if (!string.IsNullOrWhiteSpace(item.RequestedMeasure))
                {
                    var measure = new W.Paragraph(new W.ParagraphProperties(
                        new W.Indentation { Left = "560" },
                        new W.SpacingBetweenLines { After = "60" }));
                    measure.AppendChild(MakeRun($"Medida solicitada: {item.RequestedMeasure}",
                        bold: true, italic: false, underline: false, colorHex: "#C00000", size: 20));
                    cursor = InsertAfter(cursor, measure);
                }

                if (isTechnical)
                {
                    if (!string.IsNullOrWhiteSpace(item.TechnicalNotes))
                    {
                        var notes = new W.Paragraph(new W.ParagraphProperties(
                            new W.Indentation { Left = "560" },
                            new W.SpacingBetweenLines { After = "60" }));
                        notes.AppendChild(MakeRun($"Notas técnicas: {item.TechnicalNotes}",
                            bold: false, italic: true, underline: false, colorHex: "#1F68C7", size: 20));
                        cursor = InsertAfter(cursor, notes);
                    }
                }
                else
                {
                    cursor = InsertAfter(cursor, BuildCostTable(item));
                    cursor = InsertAfter(cursor, new W.Paragraph()); // aire después de la tabla
                }
            }

            marker.Remove();
        }

        private static W.Table BuildCostTable(OrderItem item)
        {
            var table = new W.Table(
                new W.TableProperties(
                    new W.TableBorders(
                        new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 }),
                    new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" }));

            var header = new W.TableRow();
            foreach (var text in new[] { "Cantidad", "Días", "Costo Unitario", "Costo Total" })
                header.AppendChild(MakeHeaderCell(text));
            table.AppendChild(header);

            var row = new W.TableRow();
            foreach (var text in new[]
                     {
                         item.Quantity.ToString(), item.Dias.ToString(),
                         MoneyFormatter.Currency(item.UnitPrice), MoneyFormatter.Currency(item.Total)
                     })
            {
                var p = new W.Paragraph(new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }));
                p.AppendChild(MakeRun(text, bold: false, italic: false, underline: false, colorHex: "#000000", size: 20));
                row.AppendChild(new W.TableCell(p));
            }
            table.AppendChild(row);
            return table;
        }

        private static W.TableCell MakeHeaderCell(string text)
        {
            var p = new W.Paragraph(new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }));
            p.AppendChild(MakeRun(text, bold: true, italic: false, underline: false, colorHex: "#FFFFFF", size: 20));
            return new W.TableCell(
                new W.TableCellProperties(new W.Shading
                {
                    Val = W.ShadingPatternValues.Clear,
                    Fill = "1F4E79" // cabecera azul, mismo espíritu que la tabla del motor COM
                }),
                p);
        }

        private static W.Run MakeRun(string text, bool bold, bool italic, bool underline, string colorHex, int size)
        {
            var props = new W.RunProperties(
                new W.RunFonts { Ascii = "Montserrat", HighAnsi = "Montserrat" },
                new W.FontSize { Val = size.ToString() });
            if (bold) props.AppendChild(new W.Bold());
            if (italic) props.AppendChild(new W.Italic());
            if (underline) props.AppendChild(new W.Underline { Val = W.UnderlineValues.Single });
            props.AppendChild(new W.Color { Val = (colorHex ?? "#000000").TrimStart('#') });

            var run = new W.Run(props);
            // '\n' embebido (notas técnicas, comentarios) no genera salto dentro de un
            // W.Text: cada línea va en su propio W.Text separado por W.Break.
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) run.AppendChild(new W.Break());
                run.AppendChild(new W.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
            return run;
        }

        private static OpenXmlElement InsertAfter(OpenXmlElement anchor, OpenXmlElement element)
        {
            anchor.InsertAfterSelf(element);
            return element;
        }

        // ── Imagen flotante (paridad con el motor COM) ───────────────

        private const long ImageSizeEmu = 576000L; // 1.6 cm (1 cm = 360.000 EMU)

        private static PartTypeInfo ImagePartTypeFor(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => ImagePartType.Png,
                ".jpg" or ".jpeg" => ImagePartType.Jpeg,
                ".gif" => ImagePartType.Gif,
                ".bmp" => ImagePartType.Bmp,
                _ => ImagePartType.Png,
            };

        /// <summary>
        /// Run con un wp:anchor flotante: imagen de 1.6×1.6 cm detrás del texto
        /// (behindDoc, equivalente al wdWrapBehind del motor COM), anclada al párrafo
        /// del título en la esquina izquierda de la columna.
        /// </summary>
        private static W.Run BuildFloatingImageRun(MainDocumentPart mainPart, string imagePath, uint drawingId)
        {
            var imagePart = mainPart.AddImagePart(ImagePartTypeFor(imagePath));
            using (var stream = File.OpenRead(imagePath))
                imagePart.FeedData(stream);
            string relId = mainPart.GetIdOfPart(imagePart);

            var anchor = new DW.Anchor(
                new DW.SimplePosition { X = 0L, Y = 0L },
                new DW.HorizontalPosition(new DW.PositionOffset("0"))
                { RelativeFrom = DW.HorizontalRelativePositionValues.Column },
                new DW.VerticalPosition(new DW.PositionOffset("0"))
                { RelativeFrom = DW.VerticalRelativePositionValues.Paragraph },
                new DW.Extent { Cx = ImageSizeEmu, Cy = ImageSizeEmu },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.WrapNone(),
                new DW.DocProperties { Id = drawingId, Name = $"ProductoImg{drawingId}" },
                new DW.NonVisualGraphicFrameDrawingProperties(
                    new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"ProductoImg{drawingId}" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = ImageSizeEmu, Cy = ImageSizeEmu }),
                                new A.PresetGeometry(new A.AdjustValueList())
                                { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U,
                SimplePos = false,
                RelativeHeight = 0U,
                BehindDoc = true, // detrás del texto, como wdWrapBehind en COM
                Locked = false,
                LayoutInCell = true,
                AllowOverlap = true,
            };

            return new W.Run(new W.Drawing(anchor));
        }

        private static List<CustomFieldDefinition> DeserializeFields(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<CustomFieldDefinition>();
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<CustomFieldDefinition>>(json)
                       ?? new List<CustomFieldDefinition>();
            }
            catch
            {
                return new List<CustomFieldDefinition>();
            }
        }
    }
}
