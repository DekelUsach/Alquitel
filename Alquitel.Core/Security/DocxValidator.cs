using System;

namespace Alquitel.Core.Security
{
    /// <summary>
    /// Verificación de contenido para los .docx que la app descarga del bucket de
    /// plantillas antes de guardarlos en el cache local y abrirlos con Word.
    ///
    /// La plantilla es el vector más peligroso de la app: Word abre lo que le den. Un
    /// objeto equivocado (una página de error HTML del gateway, un .docm con macros, un
    /// archivo cortado a mitad de descarga) no debe llegar nunca al cache. Se valida
    /// firma ZIP, tamaño y las entradas obligatorias del formato WordprocessingML.
    /// </summary>
    public static class DocxValidator
    {
        /// <summary>Un .docx real nunca pesa menos que su propio contenedor mínimo.</summary>
        public const int MinSizeBytes = 512;

        /// <summary>Tope de tamaño: evita agotar memoria con una respuesta gigante.</summary>
        public const int MaxSizeBytes = 64 * 1024 * 1024;

        private static ReadOnlySpan<byte> ZipMagic => new byte[] { 0x50, 0x4B, 0x03, 0x04 }; // "PK\x03\x04"

        public static bool IsValidDocx(byte[]? bytes) =>
            bytes != null && IsValidDocx(new ReadOnlySpan<byte>(bytes));

        public static bool IsValidDocx(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < MinSizeBytes || bytes.Length > MaxSizeBytes) return false;
            if (!bytes.StartsWith(ZipMagic)) return false;

            // Los nombres de entrada de un ZIP viajan sin comprimir tanto en la cabecera
            // local como en el directorio central, así que se pueden buscar literalmente.
            return Contains(bytes, "[Content_Types].xml") && Contains(bytes, "word/document.xml");
        }

        /// <summary>Motivo legible del rechazo, o null si el contenido es un .docx válido.</summary>
        public static string? Describe(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return "La descarga vino vacía.";
            if (bytes.Length < MinSizeBytes) return $"El archivo pesa {bytes.Length} bytes: es demasiado chico para ser un .docx.";
            if (bytes.Length > MaxSizeBytes) return "El archivo supera el tamaño máximo admitido (64 MB).";
            if (!new ReadOnlySpan<byte>(bytes).StartsWith(ZipMagic)) return "El contenido descargado no es un documento de Word (falta la firma ZIP).";
            if (!Contains(bytes, "[Content_Types].xml") || !Contains(bytes, "word/document.xml"))
                return "El archivo es un ZIP pero no tiene la estructura de un documento de Word.";
            return null;
        }

        private static bool Contains(ReadOnlySpan<byte> haystack, string needleAscii)
        {
            Span<byte> needle = stackalloc byte[needleAscii.Length];
            for (int i = 0; i < needleAscii.Length; i++) needle[i] = (byte)needleAscii[i];
            return haystack.IndexOf(needle) >= 0;
        }
    }
}
