using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Alquitel.UI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private static readonly string SettingsFilePath = AppPaths.SettingsFilePath;

        [ObservableProperty]
        private string _presupuestosFolder = AppPaths.DefaultPresupuestosFolder;

        [ObservableProperty]
        private string _presupuestosTemplate = AppPaths.DefaultPresupuestosTemplate;

        [ObservableProperty]
        private string _ofFolder = AppPaths.DefaultOfFolder;

        [ObservableProperty]
        private string _ofTemplate = AppPaths.DefaultOfTemplate;

        [ObservableProperty]
        private string _otFolder = AppPaths.DefaultOtFolder;

        [ObservableProperty]
        private string _otTemplate = AppPaths.DefaultOtTemplate;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public SettingsViewModel()
        {
            LoadSettings();
        }

        [RelayCommand]
        private void BrowsePresupuestosFolder() => BrowseFolder(path => PresupuestosFolder = path);

        [RelayCommand]
        private void BrowsePresupuestosTemplate() => BrowseFile(path => PresupuestosTemplate = path);

        [RelayCommand]
        private void BrowseOfFolder() => BrowseFolder(path => OfFolder = path);

        [RelayCommand]
        private void BrowseOfTemplate() => BrowseFile(path => OfTemplate = path);

        [RelayCommand]
        private void BrowseOtFolder() => BrowseFolder(path => OtFolder = path);

        [RelayCommand]
        private void BrowseOtTemplate() => BrowseFile(path => OtTemplate = path);

        [RelayCommand]
        private void SaveSettings()
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
                };
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
                StatusMessage = "✓ Configuración guardada correctamente.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Error al guardar: {ex.Message}";
            }
        }

        public void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (settings == null) return;

                if (settings.TryGetValue("PresupuestosFolder", out var pf)) PresupuestosFolder = pf;
                if (settings.TryGetValue("PresupuestosTemplate", out var pt)) PresupuestosTemplate = pt;
                if (settings.TryGetValue("OfFolder", out var of)) OfFolder = of;
                if (settings.TryGetValue("OfTemplate", out var ot2)) OfTemplate = ot2;
                if (settings.TryGetValue("OtFolder", out var otf)) OtFolder = otf;
                if (settings.TryGetValue("OtTemplate", out var ott)) OtTemplate = ott;
            }
            catch (Exception ex) { AppLog.Warning(ex, "SettingsViewModel.LoadSettings failed"); }
        }

        /// <summary>
        /// Returns the current settings as a dictionary without showing MessageBox.
        /// Used by BudgetBuilderViewModel for document generation paths.
        /// </summary>
        public Dictionary<string, string> GetCurrentPaths() => new()
        {
            ["PresupuestosFolder"] = PresupuestosFolder,
            ["PresupuestosTemplate"] = PresupuestosTemplate,
            ["OfFolder"] = OfFolder,
            ["OfTemplate"] = OfTemplate,
            ["OtFolder"] = OtFolder,
            ["OtTemplate"] = OtTemplate,
        };

        private void BrowseFolder(Action<string> setter)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Seleccionar carpeta" };
            if (dialog.ShowDialog() == true)
                setter(dialog.FolderName);
        }

        private void BrowseFile(Action<string> setter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Seleccionar plantilla",
                Filter = "Documentos Word (*.docx)|*.docx|Todos los archivos (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
                setter(dialog.FileName);
        }
    }
}
