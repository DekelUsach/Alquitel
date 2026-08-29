using System;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Validación de los datos de contacto de un cliente. El email importa más de lo
    /// que parece: es a donde salen el borrador de Outlook y el link de aprobación, y
    /// un typo se descubría recién cuando el presupuesto nunca era contestado.
    ///
    /// Deliberadamente permisiva (no implementa RFC 5322): rechaza lo que seguro está
    /// mal, no lo que es raro pero legal.
    /// </summary>
    public static class ContactValidator
    {
        /// <summary>
        /// null si el email es aceptable o está vacío (es opcional); si no, el motivo
        /// del rechazo listo para mostrar.
        /// </summary>
        public static string? ValidateEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null; // campo opcional

            var value = email.Trim();
            if (value.Length > 254) return "El email es demasiado largo.";
            if (value.Contains(' ')) return "El email no puede tener espacios.";

            int at = value.IndexOf('@');
            if (at <= 0 || at != value.LastIndexOf('@'))
                return "El email debe tener un único @ con texto antes.";

            var domain = value[(at + 1)..];
            if (domain.Length < 3 || !domain.Contains('.'))
                return "El dominio del email parece incompleto (falta el punto).";
            if (domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains(".."))
                return "El dominio del email tiene puntos mal ubicados.";

            return null;
        }

        /// <summary>
        /// null si el teléfono es aceptable o está vacío. Acepta el formato argentino
        /// con o sin +54, con espacios, guiones y paréntesis.
        /// </summary>
        public static string? ValidatePhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null; // campo opcional

            int digits = 0;
            foreach (char c in phone)
            {
                if (char.IsDigit(c)) { digits++; continue; }
                if (c is ' ' or '-' or '(' or ')' or '+' or '.' or '/') continue;
                return "El teléfono solo puede tener números, espacios, guiones y paréntesis.";
            }

            if (digits < 6) return "El teléfono tiene muy pocos dígitos.";
            if (digits > 15) return "El teléfono tiene demasiados dígitos.";
            return null;
        }
    }
}
