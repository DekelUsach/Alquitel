using System;
using System.Collections.Generic;

namespace Alquitel.Core.Entities
{
    public class CustomFieldDefinition
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        
        // Formatting
        public bool IsBold { get; set; }
        public bool IsUnderline { get; set; }
        public string ColorHex { get; set; } = "#FFFFFF"; // e.g., #91c991, #FF0000
    }
}
