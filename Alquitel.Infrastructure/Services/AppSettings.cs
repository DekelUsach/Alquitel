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

        public string PresupuestosFolder { get; set; } = AppPaths.DefaultPresupuestosFolder;
        public string PresupuestosTemplate { get; set; } = AppPaths.DefaultPresupuestosTemplate;
        public string OfFolder { get; set; } = AppPaths.DefaultOfFolder;
        public string OfTemplate { get; set; } = AppPaths.DefaultOfTemplate;
        public string OtFolder { get; set; } = AppPaths.DefaultOtFolder;
        public string OtTemplate { get; set; } = AppPaths.DefaultOtTemplate;
        public bool IsDarkMode { get; set; }
        public bool ExportPdf { get; set; } = true;
        public DateTime? LastWeeklySummary { get; set; }
        public List<string> SmartSearchStopWords { get; set; } = new List<string> { "de", "para", "con", "el", "la", "los", "las", "un", "una", "y", "o", "plus", "pro", "edition", "business", "servicio" };
        public double SmartSearchThreshold { get; set; } = 4.0;
        // Minimum score gap between the best and second-best candidate before a
        // match is considered unambiguous.
        public double SmartSearchMargin { get; set; } = 0.35;

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
                    if (settings.TryGetValue("ExportPdf", out var expPdf) && bool.TryParse(expPdf, out var ePdf))
                        ExportPdf = ePdf;

                    if (settings.TryGetValue("LastWeeklySummary", out var lws) &&
                        DateTime.TryParse(lws, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var lwsDate))
                        LastWeeklySummary = lwsDate;
                    
                    if (settings.TryGetValue("SmartSearchThreshold", out var thresh) && double.TryParse(thresh, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var th))
                        SmartSearchThreshold = th;

                    if (settings.TryGetValue("SmartSearchMargin", out var marginRaw) && double.TryParse(marginRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var mg))
                        SmartSearchMargin = mg;

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
                    ["ExportPdf"] = ExportPdf.ToString(),
                    ["LastWeeklySummary"] = LastWeeklySummary?.ToString("yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    ["SmartSearchThreshold"] = SmartSearchThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["SmartSearchMargin"] = SmartSearchMargin.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
