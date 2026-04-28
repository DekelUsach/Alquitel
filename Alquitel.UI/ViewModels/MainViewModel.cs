using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Infrastructure;
using Alquitel.Infrastructure.Persistence;
using Alquitel.Core.Interfaces;
using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Windows;

namespace Alquitel.UI.ViewModels
{
    /// <summary>
    /// Shell ViewModel — owns navigation state, theme toggle, and spawns child ViewModels.
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly AlquitelDbContext _dbContext;
        private readonly IDocumentService _documentService;
        private readonly SettingsViewModel _settingsVm;

        private static readonly string SettingsFilePath = AppPaths.SettingsFilePath;

        [ObservableProperty]
        private ObservableObject? _currentViewModel;

        [ObservableProperty]
        private bool _isDarkMode;

        [ObservableProperty]
        private string _activeSection = "Dashboard";

        public MainViewModel(AlquitelDbContext dbContext, IDocumentService documentService)
        {
            _dbContext = dbContext;
            _documentService = documentService;
            _settingsVm = new SettingsViewModel();

            // Load theme preference from settings
            LoadThemePreference();
            ApplyTheme(IsDarkMode);

            // Start on Dashboard
            NavigateToDashboard();
        }

        // ── Navigation Commands ──────────────────────────────────────

        [RelayCommand]
        private void NavigateToDashboard()
        {
            ActiveSection = "Dashboard";
            CurrentViewModel = new DashboardViewModel(_dbContext, () => NavigateToBuilder());
        }

        [RelayCommand]
        private void NavigateToBuilder()
        {
            ActiveSection = "Presupuesto";
            CurrentViewModel = new BudgetBuilderViewModel(_dbContext, _documentService, _settingsVm);
        }

        [RelayCommand]
        private void NavigateToSettings()
        {
            ActiveSection = "Configuración";
            CurrentViewModel = _settingsVm;
        }

        [RelayCommand]
        private void NavigateToProducts()
        {
            ActiveSection = "Productos";
            CurrentViewModel = new ProductEditorViewModel(_dbContext);
        }

        [RelayCommand]
        private void NavigateToPresupuestos()
        {
            ActiveSection = "Presupuestos";
            CurrentViewModel = new PresupuestosViewModel(_settingsVm);
        }

        // ── Theme ────────────────────────────────────────────────────

        [RelayCommand]
        private void ToggleTheme()
        {
            IsDarkMode = !IsDarkMode;
            ApplyTheme(IsDarkMode);
            SaveThemePreferenceSilent();
        }

        private void ApplyTheme(bool isDark)
        {
            var themeFile = isDark ? "DarkTheme.xaml" : "LightTheme.xaml";
            var uri = new Uri($"pack://application:,,,/Themes/{themeFile}");
            var mergedDicts = Application.Current.Resources.MergedDictionaries;

            var toRemove = new List<ResourceDictionary>();
            foreach (var d in mergedDicts)
            {
                if (d.Source?.OriginalString.Contains("Theme.xaml") == true)
                    toRemove.Add(d);
            }
            foreach (var d in toRemove) mergedDicts.Remove(d);

            mergedDicts.Add(new ResourceDictionary { Source = uri });
        }

        private void LoadThemePreference()
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (settings != null && settings.TryGetValue("IsDarkMode", out var dm) && bool.TryParse(dm, out var isDark))
                    IsDarkMode = isDark;
            }
            catch { /* Gracefully ignore corrupt settings */ }
        }

        /// <summary>
        /// Persists the theme preference without showing any MessageBox.
        /// This fixes the bug where toggling dark mode showed "Rutas guardadas correctamente."
        /// </summary>
        private void SaveThemePreferenceSilent()
        {
            try
            {
                Dictionary<string, string> settings;
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                }
                else
                {
                    settings = new();
                }

                settings["IsDarkMode"] = IsDarkMode.ToString();
                var output = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, output);
            }
            catch { /* Silently fail — theme persistence is non-critical */ }
        }
    }
}
