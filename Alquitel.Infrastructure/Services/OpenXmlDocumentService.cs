using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
        public async Task<DocumentGenerationResult> GenerateDocumentAsync(
            Order order,
            string templatePath,
            string outputPath,
            bool isTechnical,
            bool exportPdf = false,
            IProgress<DocumentGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(order);
            progress?.Report(new DocumentGenerationProgress(
                DocumentGenerationStage.Validating, 5, "Validando plantilla"));
            var request = await Task.Run(
                () => DocumentGenerationSafety.Validate(
                    templatePath, outputPath, allowLegacyProductBookmark: false, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            try
            {
                return await Task.Run(() =>
                {
                // Se trabaja sobre un temporal y se mueve al final: editar in-place sobre
                // outputPath dejaba un .docx corrupto en la carpeta del usuario (que
                // PresupuestosView cataloga como válido) si algo fallaba a mitad.
                var warnings = request.Warnings.ToList();
                string tempPath = DocumentGenerationSafety.CreateStagingPath(
                    request.RequestedOutputPath, ".docx");
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new DocumentGenerationProgress(
                        DocumentGenerationStage.Preparing, 15, "Preparando documento"));
                    CopyWithFlush(request.TemplatePath, tempPath);

                    using (var doc = WordprocessingDocument.Open(tempPath, isEditable: true))
                    {
                        // Se valida parte por parte en vez de encadenar con "?.": una
                        // plantilla sin MainDocumentPart y una sin Document son fallas
                        // distintas y ambas terminaban en un NullReferenceException crudo.
                        var mainPart = doc.MainDocumentPart
                            ?? throw new InvalidOperationException("La plantilla no tiene contenido principal (MainDocumentPart).");
                        var body = mainPart.Document?.Body
                            ?? throw new InvalidOperationException("La plantilla no tiene cuerpo de documento.");

                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new DocumentGenerationProgress(
                            DocumentGenerationStage.ReplacingFields, 35, "Completando datos"));
                        ReplacePlaceholders(doc, order, isTechnical);
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new DocumentGenerationProgress(
                            DocumentGenerationStage.RenderingProducts, 60, "Agregando productos"));
                        RenderProducts(doc, body, order, isTechnical, warnings, cancellationToken);

                        mainPart.Document.Save();
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    using (var flushed = new FileStream(
                               tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        flushed.Flush(flushToDisk: true);

                    progress?.Report(new DocumentGenerationProgress(
                        DocumentGenerationStage.Saving, 85, "Publicando documento"));
                    var published = DocumentGenerationSafety.Publish(
                        tempPath, stagedPdfPath: null, request.RequestedOutputPath, cancellationToken);

                    if (exportPdf)
                    {
                        warnings.Add("El motor OpenXML no genera PDF; se creó únicamente el documento .docx.");
                        AppLog.Warning("Motor OpenXML: exportación a PDF no soportada; se generó solo DOCX");
                    }

                    progress?.Report(new DocumentGenerationProgress(
                        DocumentGenerationStage.Completed, 100, "Documento generado"));
                    AppLog.Information("Documento generado con motor OpenXML");
                    return new DocumentGenerationResult(
                        published.DocumentPath, published.PdfPath, warnings.AsReadOnly());
                }
                finally
                {
                    DocumentGenerationSafety.TryDelete(tempPath);
                }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DocumentGenerationSafety.TryDelete(request.TemplatePath);
            }
        }

        private static void CopyWithFlush(string sourcePath, string destinationPath)
        {
            using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = new FileStream(
                destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.WriteThrough);
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
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
            var main = doc.MainDocumentPart;
            if (main == null) yield break;
            if (main.Document?.Body != null) yield return main.Document.Body;
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

        private static void RenderProducts(
            WordprocessingDocument doc,
            W.Body body,
            Order order,
            bool isTechnical,
            ICollection<string> warnings,
            CancellationToken cancellationToken)
        {
            var marker = body.Descendants<W.Paragraph>()
                .FirstOrDefault(p => string.Concat(p.Descendants<W.Text>().Select(t => t.Text))
                    .Contains("{{PRODUCTOS_AQUI}}"));
            if (marker == null) return;

            var markerTexts = marker.Descendants<W.Text>().ToList();
            var markerParagraphText = string.Concat(markerTexts.Select(text => text.Text));
            var remainingMarkerText = markerParagraphText.Replace("{{PRODUCTOS_AQUI}}", string.Empty);
            if (markerTexts.Count > 0)
            {
                SetRunText(markerTexts[0], remainingMarkerText);
                for (var i = 1; i < markerTexts.Count; i++) markerTexts[i].Text = string.Empty;
            }
            var removeMarkerParagraph = string.IsNullOrWhiteSpace(remainingMarkerText);

            uint drawingId = 9000; // IDs únicos para los DocProperties de las imágenes
            var cursor = (OpenXmlElement)marker;
            foreach (var item in order.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Título con estilos BBCode del snapshot
                var title = new W.Paragraph(new W.ParagraphProperties(
                    new W.SpacingBetweenLines { After = "120", Before = "160" },
                    new W.Indentation { Left = "1077" })); // 1.9 cm, mismo margen que el motor COM
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
                if (!string.IsNullOrEmpty(item.ImagePath))
                {
                    if (DocumentGenerationSafety.TryCreateImageSnapshot(
                            item.ImagePath, out var imageSnapshot, out var imageWarning))
                    {
                        using (imageSnapshot)
                        {
                            try
                            {
                                title.AppendChild(BuildFloatingImageRun(
                                    doc.MainDocumentPart!, imageSnapshot!.Path, ++drawingId));
                            }
                            catch (Exception ex)
                            {
                                AppLog.Warning(
                                    "No se pudo insertar una imagen de producto en OpenXML ({ErrorType})",
                                    ex.GetType().Name);
                                warnings.Add("Se omitió una imagen de producto que no pudo insertarse.");
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(imageWarning))
                    {
                        warnings.Add(imageWarning);
                    }
                }
                cursor = InsertAfter(cursor, title);

                // Campos técnicos / specs
                foreach (var field in DeserializeFields(item.CustomFieldsJson))
                {
                    var spec = new W.Paragraph(new W.ParagraphProperties(
                        new W.SpacingBetweenLines { After = "40" },
                        new W.Indentation { Left = "560" }));
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
                        new W.SpacingBetweenLines { After = "60" },
                        new W.Indentation { Left = "560" }));
                    measure.AppendChild(MakeRun($"Medida solicitada: {item.RequestedMeasure}",
                        bold: true, italic: false, underline: false, colorHex: "#C00000", size: 20));
                    cursor = InsertAfter(cursor, measure);
                }

                if (isTechnical)
                {
                    if (!string.IsNullOrWhiteSpace(item.TechnicalNotes))
                    {
                        var notes = new W.Paragraph(new W.ParagraphProperties(
                            new W.SpacingBetweenLines { After = "60" },
                            new W.Indentation { Left = "560" }));
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

            if (removeMarkerParagraph) marker.Remove();
        }

        private static W.Table BuildCostTable(OrderItem item)
        {
            var table = new W.Table(
                new W.TableProperties(
                    new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" },
                    new W.TableBorders(
                        new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 },
                        new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })),
                new W.TableGrid(
                    new W.GridColumn { Width = "1250" },
                    new W.GridColumn { Width = "1250" },
                    new W.GridColumn { Width = "1250" },
                    new W.GridColumn { Width = "1250" }));

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
                new W.RunFonts { Ascii = "Montserrat", HighAnsi = "Montserrat" });
            if (bold) props.AppendChild(new W.Bold());
            if (italic) props.AppendChild(new W.Italic());
            if (underline) props.AppendChild(new W.Underline { Val = W.UnderlineValues.Single });
            props.AppendChild(new W.Color { Val = (colorHex ?? "#000000").TrimStart('#') });
            props.AppendChild(new W.FontSize { Val = size.ToString() });

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
