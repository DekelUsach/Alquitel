using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Alquitel.Infrastructure.Security
{
    /// <summary>
    /// Envoltorio mínimo de DPAPI (CryptProtectData/CryptUnprotectData de crypt32.dll)
    /// en el ámbito del usuario actual de Windows.
    ///
    /// Se usa P/Invoke en vez del paquete System.Security.Cryptography.ProtectedData para
    /// no sumar una dependencia NuGet por dos llamadas; el proyecto ya es net8.0-windows.
    ///
    /// Qué garantiza: los bytes protegidos solo se pueden descifrar con el perfil del
    /// mismo usuario de Windows en la misma máquina. Copiar el archivo a otra PC, o
    /// leerlo desde otra cuenta, no sirve de nada.
    /// </summary>
    internal static class DpapiProtector
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

        public static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

        private static byte[] Transform(byte[] input, bool protect)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var inBlob = new DataBlob();
            var outBlob = new DataBlob();
            try
            {
                inBlob.cbData = input.Length;
                inBlob.pbData = Marshal.AllocHGlobal(Math.Max(1, input.Length));
                Marshal.Copy(input, 0, inBlob.pbData, input.Length);

                bool ok = protect
                    ? CryptProtectData(ref inBlob, "Alquitel session key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out outBlob);

                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
    }
}
