using Alquitel.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Alquitel.Infrastructure.Services
{
    public class AppSettings : IAppSettings
    {
        private readonly string _settingsFilePath;

        public string PresupuestosFolder { get; set; } = @"C:\Alquitel\1_PRESUPUESTOS";
        public string PresupuestosTemplate { get; set; } = @"C:\Alquitel\template.docx";
        public string OfFolder { get; set; } = @"C:\Alquitel\2_OF";
        public string OfTemplate { get; set; } = @"C:\Alquitel\template_of.docx";
        public string OtFolder { get; set; } = @"C:\Alquitel\3_OT";
        public string OtTemplate { get; set; } = @"C:\Alquitel\template_ot.docx";
        public bool IsDarkMode { get; set; }
        public List<string> SmartSearchStopWords { get; set; } = new List<string> { "de", "para", "con", "el", "la", "los", "las", "un", "una", "y", "o", "plus", "pro", "edition", "business", "servicio" };
        public double SmartSearchThreshold { get; set; } = 4.0;

        public AppSettings(string settingsFilePath)
        {
            _settingsFilePath = settingsFilePath;
            LoadSettings();
        }

        public void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath)) return;
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (settings != null)
                {
                    if (settings.TryGetValue("PresupuestosFolder", out var f)) PresupuestosFolder = f;
                    if (settings.TryGetValue("PresupuestosTemplate", out var t)) PresupuestosTemplate = t;
                    if (settings.TryGetValue("OfFolder", out var of)) OfFolder = of;
                    if (settings.TryGetValue("OfTemplate", out var oft)) OfTemplate = oft;
                    if (settings.TryGetValue("OtFolder", out var ot)) OtFolder = ot;
                    if (settings.TryGetValue("OtTemplate", out var ott)) OtTemplate = ott;
                    if (settings.TryGetValue("IsDarkMode", out var dm) && bool.TryParse(dm, out var isDark))
                        IsDarkMode = isDark;
                    
                    if (settings.TryGetValue("SmartSearchThreshold", out var thresh) && double.TryParse(thresh, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var th))
                        SmartSearchThreshold = th;

                    if (settings.TryGetValue("SmartSearchStopWords", out var stopWordsJson))
                    {
                        try { SmartSearchStopWords = JsonSerializer.Deserialize<List<string>>(stopWordsJson) ?? SmartSearchStopWords; }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "LoadSettings: corrupt settings.json");
            }
        }

        public void SaveSettings()
        {
            try
            {
                var settings = new Dictionary<string, string>
                {
                    ["PresupuestosFolder"] = PresupuestosFolder,
                    ["PresupuestosTemplate"] = PresupuestosTemplate,
                    ["OfFolder"] = OfFolder,
                    ["OfTemplate"] = OfTemplate,
                    ["OtFolder"] = OtFolder,
                    ["OtTemplate"] = OtTemplate,
                    ["IsDarkMode"] = IsDarkMode.ToString(),
                    ["SmartSearchThreshold"] = SmartSearchThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["SmartSearchStopWords"] = JsonSerializer.Serialize(SmartSearchStopWords)
                };
                var output = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, output);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "SaveSettings failed");
            }
        }
    }
}
