using System;

namespace Alquitel.Core.Helpers
{
    public static class CuitValidator
    {
        public static bool IsValid(string? cuit)
        {
            var digits = Normalize(cuit);
            if (digits == null) return false;

            int[] multipliers = { 5, 4, 3, 2, 7, 6, 5, 4, 3, 2 };
            int total = 0;

            for (int i = 0; i < 10; i++)
            {
                total += (digits[i] - '0') * multipliers[i];
            }

            int rest = total % 11;
            int verificationCode = rest == 0 ? 0 : (rest == 1 ? 9 : 11 - rest);

            return verificationCode == (digits[10] - '0');
        }

        /// <summary>
        /// Devuelve los 11 dígitos del CUIT (sin guiones, espacios ni puntos), o null si
        /// la entrada no tiene exactamente 11 dígitos. Solo acepta caracteres ASCII 0-9:
        /// un signo o un tabulador colados en el string hacían explotar el parseo por
        /// carácter del algoritmo de verificación.
        /// </summary>
        public static string? Normalize(string? cuit)
        {
            if (string.IsNullOrWhiteSpace(cuit)) return null;

            Span<char> buffer = stackalloc char[11];
            int written = 0;
            foreach (char c in cuit)
            {
                if (c == '-' || c == ' ' || c == '.' || c == '/') continue;
                if (c < '0' || c > '9') return null;
                if (written == 11) return null; // más de 11 dígitos
                buffer[written++] = c;
            }

            return written == 11 ? new string(buffer) : null;
        }

        /// <summary>
        /// Formato canónico de AFIP "XX-XXXXXXXX-X". Si la entrada no es un CUIT de 11
        /// dígitos se devuelve tal cual vino (no se inventa un formato sobre basura).
        /// </summary>
        public static string Format(string? cuit)
        {
            var digits = Normalize(cuit);
            if (digits == null) return cuit ?? string.Empty;
            return $"{digits[..2]}-{digits[2..10]}-{digits[10]}";
        }
    }
}
