using System;
using System.Collections.Generic;
using Alquitel.Core.Parsing;

namespace Alquitel.Infrastructure.Services.WordInterop
{
    public static class TagParserInterop
    {
        // Word color values are BGR: 0x00BBGGRR
        public const int WD_WHITE   = 0x00FFFFFF;
        public const int WD_BLACK   = 0x00000000;
        public const int WD_AUTO    = -16777216; // wdColorAutomatic
        public const int WD_RED     = 0x000000FF; // #FF0000
        public const int WD_GREEN   = 0x00006600; // #006600
        public const int WD_DARKRED = 0x000000C0; // #C00000
        public const int WD_BLUE    = 0x00C7681F; // #1F68C7

        public static int HexToBgr(string hex, int fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(hex) || !hex.StartsWith("#") || hex.Length != 7) return fallback;
                int r = Convert.ToInt32(hex.Substring(1, 2), 16);
                int g = Convert.ToInt32(hex.Substring(3, 2), 16);
                int b = Convert.ToInt32(hex.Substring(5, 2), 16);
                return r | (g << 8) | (b << 16);
            }
            catch { return fallback; }
        }
    }
}
