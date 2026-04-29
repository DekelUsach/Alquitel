using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Alquitel.Core.Parsing
{
    public sealed class TextSegment
    {
        public string Text { get; set; } = string.Empty;
        public string ColorHex { get; set; } = "#000000";
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
    }

    public static class TagParser
    {
        public static List<TextSegment> Parse(string? text, string defaultColorHex = "#000000", bool defaultBold = false, bool defaultUnderline = false)
        {
            var result = new List<TextSegment>();
            if (string.IsNullOrEmpty(text))
            {
                result.Add(new TextSegment { ColorHex = defaultColorHex, Bold = defaultBold, Underline = defaultUnderline });
                return result;
            }

            string color = defaultColorHex;
            bool bold = defaultBold, italic = false, underline = defaultUnderline;
            var stack = new Stack<(string color, bool bold, bool italic, bool underline)>();

            int i = 0;
            var buf = new StringBuilder();
            void Flush()
            {
                if (buf.Length == 0) return;
                result.Add(new TextSegment { Text = buf.ToString(), ColorHex = color, Bold = bold, Italic = italic, Underline = underline });
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
                        string? newColor = name switch
                        {
                            "red"     => "#FF0000",
                            "green"   => "#006600",
                            "darkred" => "#C00000",
                            "blue"    => "#1F68C7",
                            "white"   => "#FFFFFF",
                            "black"   => "#000000",
                            _ => null
                        };
                        bool isStyle = name == "b" || name == "i" || name == "u";

                        if (newColor != null || isStyle)
                        {
                            Flush();
                            if (!isClose)
                            {
                                stack.Push((color, bold, italic, underline));
                                if (newColor != null) color = newColor;
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
            if (result.Count == 0)
                result.Add(new TextSegment { ColorHex = defaultColorHex, Bold = defaultBold, Underline = defaultUnderline });
                
            return result;
        }

        public static string? StripTags(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, @"\[/?[a-zA-Z]+\]", "");
        }
    }
}
