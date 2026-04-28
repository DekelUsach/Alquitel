using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Infrastructure;
using Alquitel.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Alquitel.UI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IAppSettings _appSettings;

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

        public SettingsViewModel(IAppSettings appSettings)
        {
            _appSettings = appSettings;
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
                _appSettings.PresupuestosFolder = PresupuestosFolder;
                _appSettings.PresupuestosTemplate = PresupuestosTemplate;
                _appSettings.OfFolder = OfFolder;
                _appSettings.OfTemplate = OfTemplate;
                _appSettings.OtFolder = OtFolder;
                _appSettings.OtTemplate = OtTemplate;
                _appSettings.SaveSettings();
                StatusMessage = "✓ Configuración guardada correctamente.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Error al guardar: {ex.Message}";
            }
        }

        public void LoadSettings()
        {
            PresupuestosFolder = _appSettings.PresupuestosFolder;
            PresupuestosTemplate = _appSettings.PresupuestosTemplate;
            OfFolder = _appSettings.OfFolder;
            OfTemplate = _appSettings.OfTemplate;
            OtFolder = _appSettings.OtFolder;
            OtTemplate = _appSettings.OtTemplate;
        }

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
