using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Utilidad central para exportación segura de CSV compatible con Excel.
    /// Neutraliza vectores de Formula Injection (CSV Injection / DDE) y escapa
    /// caracteres especiales según RFC 4180.
    /// </summary>
    public static class CsvSanitizer
    {
        private static readonly char[] FormulaTriggers = { '=', '+', '-', '@', '\t', '\r' };

        /// <summary>
        /// Obtiene codificación UTF-8 con BOM (Byte Order Mark) para garantizar
        /// que Microsoft Excel en Windows abra el archivo con tildes y caracteres especiales correctos.
        /// </summary>
        public static Encoding ExcelUtf8Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        /// <summary>
        /// Sanitiza y escapa una celda individual para CSV.
        /// Neutraliza fórmulas anteponiendo comilla simple (') y escapa separadores, comillas y saltos de línea.
        /// </summary>
        public static string SanitizeField(string? value, char separator = ';')
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var val = value;

            // Detección y neutralización de CSV / Excel Formula Injection
            if (IsPotentialFormula(val))
            {
                val = "'" + val;
            }

            // Escapado RFC 4180: si contiene separador, comillas o saltos de línea, se encierra entre comillas
            bool needsQuotes = val.Contains(separator) ||
                               val.Contains('"') ||
                               val.Contains('\n') ||
                               val.Contains('\r') ||
                               val.StartsWith(' ') ||
                               val.EndsWith(' ') ||
                               val.StartsWith('\'');

            if (needsQuotes)
            {
                val = $"\"{val.Replace("\"", "\"\"")}\"";
            }

            return val;
        }

        /// <summary>
        /// Da formato a una fila completa uniendo los campos sanitizados con el separador configurado.
        /// </summary>
        public static string FormatRow(IEnumerable<string?> fields, char separator = ';')
        {
            if (fields == null) return string.Empty;
            return string.Join(separator.ToString(), fields.Select(f => SanitizeField(f, separator)));
        }

        /// <summary>
        /// Genera el contenido CSV completo a partir de encabezados y filas.
        /// </summary>
        public static string BuildCsv(IEnumerable<string> headers, IEnumerable<IEnumerable<string?>> rows, char separator = ';')
        {
            var sb = new StringBuilder();
            if (headers != null && headers.Any())
            {
                sb.AppendLine(FormatRow(headers, separator));
            }

            if (rows != null)
            {
                foreach (var row in rows)
                {
                    sb.AppendLine(FormatRow(row, separator));
                }
            }

            return sb.ToString();
        }

        private static bool IsPotentialFormula(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            char first = value[0];
            if (first != '=' && first != '+' && first != '-' && first != '@' && first != '\t' && first != '\r')
                return false;

            // Si comienza con '+' o '-', verificamos si es un número legítimo.
            // Los números puros no son fórmulas maliciosas en Excel.
            if (first == '+' || first == '-')
            {
                if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    decimal.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out _))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
