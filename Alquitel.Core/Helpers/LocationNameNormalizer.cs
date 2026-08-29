using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Normalización de nombres de ubicación para detectar duplicados en el padrón.
    ///
    /// Por qué existe: el campo "Lugar del evento" del armador es texto libre y
    /// <c>OrderPersistenceService</c> hace find-or-create por nombre EXACTO, sin índice
    /// único. Resultado: "La Rural", "la rural" y "La Rural " conviven como tres lugares
    /// distintos, y ese nombre se imprime en el .docx que va al cliente.
    /// </summary>
    public static class LocationNameNormalizer
    {
        /// <summary>Ubicación centinela que recibe las órdenes de lugares borrados.</summary>
        public const string SentinelName = "(Sin ubicación)";

        /// <summary>
        /// Trim → minúsculas invariantes → sin acentos → espacios colapsados.
        ///
        /// Deliberadamente NO descarta palabras de ruido ("predio", "salón", "centro")
        /// ni usa similitud difusa: la fusión de lugares es irreversible y mueve
        /// presupuestos reales, así que agrupar "Pabellón 1" con "Pabellón 4" sería
        /// mucho peor que no detectar un duplicado. Preferimos el falso negativo.
        /// </summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var lower = raw.Trim().ToLowerInvariant();

            // Descomponer para separar la letra base de su tilde y descartar las tildes.
            var decomposed = lower.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            bool lastWasSpace = false;

            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsWhiteSpace(c))
                {
                    // Colapsar cualquier run de espacios/tabs a un solo espacio.
                    if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                sb.Append(c);
                lastWasSpace = false;
            }

            // El colapso pudo dejar un espacio final si el nombre terminaba en whitespace
            // que sobrevivió al Trim (por ejemplo un espacio de ancho cero).
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// True si el nombre es el centinela, comparado en forma normalizada: una base
        /// vieja puede tener "(sin ubicacion)" sin tilde o con otra capitalización.
        /// </summary>
        public static bool IsSentinel(string? name) =>
            Normalize(name) == Normalize(SentinelName);

        /// <summary>
        /// Claves normalizadas que aparecen más de una vez en la colección. Los nombres
        /// vacíos no cuentan como duplicados entre sí: son "sin nombre", que es otro
        /// problema con su propio distintivo en la UI.
        /// </summary>
        public static HashSet<string> DuplicateKeys(IEnumerable<string?> names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var repeated = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in names)
            {
                var key = Normalize(name);
                if (key.Length == 0) continue;
                if (!seen.Add(key)) repeated.Add(key);
            }

            return repeated;
        }
    }
}
