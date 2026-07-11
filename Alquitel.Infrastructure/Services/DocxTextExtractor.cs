using System;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Alquitel.Infrastructure.Services
{
    /// <summary>
    /// Extrae el texto plano de un .docx leyendo word/document.xml directamente del
    /// contenedor ZIP, sin abrir Word ni depender de COM Interop. Usado por la vista
    /// de Órdenes de Trabajo para previsualizar documentos dentro de la app.
    /// </summary>
    public static class DocxTextExtractor
    {
        private static readonly XNamespace W =
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        public static string ExtractPlainText(string docxPath)
        {
            using var zip = ZipFile.OpenRead(docxPath);
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) return string.Empty;

            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var body = doc.Root?.Element(W + "body");
            if (body == null) return string.Empty;

            var sb = new StringBuilder();
            AppendBlock(body, sb);
            return sb.ToString().TrimEnd();
        }

        private static void AppendBlock(XElement container, StringBuilder sb)
        {
            foreach (var child in container.Elements())
            {
                if (child.Name == W + "p")
                {
                    AppendParagraph(child, sb);
                    sb.AppendLine();
                }
                else if (child.Name == W + "tbl")
                {
                    // Cada fila en una línea, celdas separadas por tabulaciones para
                    // que las tablas de la OT (cantidades, equipos) queden legibles.
                    foreach (var row in child.Elements(W + "tr"))
                    {
                        bool first = true;
                        foreach (var cell in row.Elements(W + "tc"))
                        {
                            if (!first) sb.Append('\t');
                            first = false;
                            var cellText = new StringBuilder();
                            foreach (var p in cell.Elements(W + "p"))
                            {
                                if (cellText.Length > 0) cellText.Append(' ');
                                AppendParagraph(p, cellText);
                            }
                            sb.Append(cellText);
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }
            }
        }

        private static void AppendParagraph(XElement paragraph, StringBuilder sb)
        {
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name == W + "t") sb.Append(node.Value);
                else if (node.Name == W + "tab") sb.Append('\t');
                else if (node.Name == W + "br" || node.Name == W + "cr") sb.AppendLine();
            }
        }
    }
}
