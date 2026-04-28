using System;
using System.Collections.Generic;

namespace Alquitel.Infrastructure.Services.WordInterop
{
    public sealed class Segment
    {
        public string Text = "";
        public int Color = TagParser.WD_BLACK;
        public bool Bold;
        public bool Italic;
        public bool Underline;
    }

    public static class TagParser
    {
        // Word color values are BGR: 0x00BBGGRR
        public const int WD_WHITE   = 0x00FFFFFF;
        public const int WD_BLACK   = 0x00000000;
        public const int WD_AUTO    = -16777216; // wdColorAutomatic
        public const int WD_RED     = 0x000000FF; // #FF0000
        public const int WD_GREEN   = 0x00006600; // #006600
        public const int WD_DARKRED = 0x000000C0; // #C00000
        public const int WD_BLUE    = 0x00C7681F; // #1F68C7

        public static List<Segment> ParseSegments(string? text, int defaultColor, bool defaultBold = false, bool defaultUnderline = false)
        {
            var result = new List<Segment>();
            if (string.IsNullOrEmpty(text)) return result;

            int color = defaultColor;
            bool bold = defaultBold, italic = false, underline = defaultUnderline;
            var stack = new Stack<(int color, bool bold, bool italic, bool underline)>();

            int i = 0;
            var buf = new System.Text.StringBuilder();
            void Flush()
            {
                if (buf.Length == 0) return;
                result.Add(new Segment { Text = buf.ToString(), Color = color, Bold = bold, Italic = italic, Underline = underline });
                buf.Clear();
            }

            while (i < text.Length)
            {
                if (text[i] == '[')
                {
                    int close = text.IndexOf(']', i + 1);
                    if (close > i)
                    {
                        string tag = text.Substring(i + 1, close - i - 1).Trim().ToLowerInvariant();
                        bool isClose = tag.StartsWith("/");
                        string name = isClose ? tag.Substring(1) : tag;
                        int? newColor = name switch
                        {
                            "red"     => WD_RED,
                            "green"   => WD_GREEN,
                            "darkred" => WD_DARKRED,
                            "blue"    => WD_BLUE,
                            "white"   => WD_WHITE,
                            "black"   => WD_BLACK,
                            _ => (int?)null
                        };
                        bool isStyle = name == "b" || name == "i" || name == "u";

                        if (newColor.HasValue || isStyle)
                        {
                            Flush();
                            if (!isClose)
                            {
                                stack.Push((color, bold, italic, underline));
                                if (newColor.HasValue) color = newColor.Value;
                                if (name == "b") bold = true;
                                if (name == "i") italic = true;
                                if (name == "u") underline = true;
                            }
                            else if (stack.Count > 0)
                            {
                                var s = stack.Pop();
                                color = s.color; bold = s.bold; italic = s.italic; underline = s.underline;
                            }
                            i = close + 1;
                            continue;
                        }
                    }
                }
                buf.Append(text[i]);
                i++;
            }
            Flush();
            return result;
        }

        public static string? StripTags(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z]+\]", "");
        }

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
