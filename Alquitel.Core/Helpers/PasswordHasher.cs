using System;
using System.Security.Cryptography;

namespace Alquitel.Core.Helpers
{
    /// <summary>
    /// Hashing de contraseñas con PBKDF2 (Rfc2898). Formato del hash persistido:
    /// "iteraciones.saltBase64.hashBase64". Nunca se guarda texto plano.
    /// </summary>
    public static class PasswordHasher
    {
        private const int Iterations = 100_000;
        private const int SaltSize = 16;
        private const int HashSize = 32;

        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string? storedHash)
        {
            if (string.IsNullOrWhiteSpace(storedHash)) return false;
            var parts = storedHash.Split('.');
            if (parts.Length != 3) return false;

            try
            {
                int iterations = int.Parse(parts[0]);
                var salt = Convert.FromBase64String(parts[1]);
                var expected = Convert.FromBase64String(parts[2]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
