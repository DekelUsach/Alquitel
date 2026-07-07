using System;
using System.IO;

namespace Alquitel.Infrastructure
{
    /// <summary>
    /// Centralized application path provider.
    /// Roots persistent state under %LocalAppData%\Alquitel for multi-user / portable installs.
    /// Auto-migrates legacy state from C:\Alquitel on first run if present.
    /// </summary>
    public static class AppPaths
    {
        public static string AppDataRoot { get; }
        public static string SettingsFilePath { get; }
        public static string DbFilePath { get; }
        public static string DbConnectionString { get; }
        public static string LogsFolder { get; }
        public static string BackupsFolder { get; }
        /// <summary>Cache local de plantillas descargadas de Supabase Storage.</summary>
        public static string TemplatesCacheFolder { get; }
        public static string DefaultPresupuestosFolder { get; }
        public static string DefaultOfFolder { get; }
        public static string DefaultOtFolder { get; }
        public static string DefaultPresupuestosTemplate { get; }
        public static string DefaultOfTemplate { get; }
        public static string DefaultOtTemplate { get; }

        private const string LegacyRoot = @"C:\Alquitel";

        static AppPaths()
        {
            AppDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Alquitel");
            Directory.CreateDirectory(AppDataRoot);

            LogsFolder = Path.Combine(AppDataRoot, "logs");
            BackupsFolder = Path.Combine(AppDataRoot, "backups");
            TemplatesCacheFolder = Path.Combine(AppDataRoot, "templates_cache");
            Directory.CreateDirectory(LogsFolder);
            Directory.CreateDirectory(BackupsFolder);
            Directory.CreateDirectory(TemplatesCacheFolder);

            SettingsFilePath = Path.Combine(AppDataRoot, "settings.json");
            DbFilePath = Path.Combine(AppDataRoot, "Alquitel.db");
            DbConnectionString = $"Data Source={DbFilePath}";

            DefaultPresupuestosFolder = Path.Combine(AppDataRoot, "1_PRESUPUESTOS");
            DefaultOfFolder = Path.Combine(AppDataRoot, "2_OF");
            DefaultOtFolder = Path.Combine(AppDataRoot, "3_OT");

            DefaultPresupuestosTemplate = Path.Combine(DefaultPresupuestosFolder, "template.docx");
            DefaultOfTemplate = Path.Combine(AppDataRoot, "OF_template.docx");
            DefaultOtTemplate = Path.Combine(AppDataRoot, "OT_template.docx");

            try { MigrateLegacyState(); } catch { /* migration is best-effort */ }
        }

        private static void MigrateLegacyState()
        {
            if (!Directory.Exists(LegacyRoot)) return;

            TryCopy(Path.Combine(LegacyRoot, "settings.json"), SettingsFilePath);
            TryCopy(Path.Combine(LegacyRoot, "Alquitel.db"), DbFilePath);
        }

        private static void TryCopy(string src, string dst)
        {
            try
            {
                if (!File.Exists(src) || File.Exists(dst)) return;
                File.Copy(src, dst, overwrite: false);
            }
            catch { /* legacy file locked or missing */ }
        }
    }
}
