using System;
using System.IO;

namespace Alquitel.Infrastructure
{
    /// <summary>
    /// Centralizes path sanitization to prevent path traversal in Process.Start and file I/O.
    /// </summary>
    public static class PathValidator
    {
        /// <summary>
        /// Returns true when <paramref name="candidatePath"/> is a canonicalized .docx file
        /// that resides strictly within <paramref name="allowedRoot"/>.
        /// Defenses: null-byte rejection, canonicalization via Path.GetFullPath, directory prefix check.
        /// </summary>
        public static bool IsDocxWithinRoot(string? candidatePath, string? allowedRoot)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(allowedRoot))
                return false;

            // Null-byte injection guard
            if (candidatePath.Contains('\0') || allowedRoot.Contains('\0'))
                return false;

            if (!candidatePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                return false;

            string normalizedFile;
            string normalizedRoot;
            try
            {
                normalizedFile = Path.GetFullPath(candidatePath);
                normalizedRoot = Path.GetFullPath(allowedRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            }
            catch
            {
                return false;
            }

            return normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the canonicalized absolute path, or null if the input is unsafe/invalid.
        /// </summary>
        public static string? Canonicalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
                return null;
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }
    }
}
