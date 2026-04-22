using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace Alquitel.UI.ViewModels
{
    public partial class PresupuestosViewModel : ObservableObject, IDisposable
    {
        private readonly SettingsViewModel _settingsVm;
        private readonly ICollectionView _filesView;
        private FileSystemWatcher? _watcher;

        [ObservableProperty]
        private string _folderPath = string.Empty;

        [ObservableProperty]
        private string _filterText = string.Empty;

        [ObservableProperty]
        private DateTime? _filterDateFrom;

        [ObservableProperty]
        private DateTime? _filterDateTo;

        [ObservableProperty]
        private PresupuestoFile? _selectedFile;

        [ObservableProperty]
        private bool _isDetailPanelOpen;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public ObservableCollection<PresupuestoFile> Files { get; } = new();
        public ICollectionView FilesView => _filesView;

        public PresupuestosViewModel(SettingsViewModel settingsVm)
        {
            _settingsVm = settingsVm;

            _filesView = CollectionViewSource.GetDefaultView(Files);
            _filesView.Filter = FilterFile;
            _filesView.SortDescriptions.Add(
                new SortDescription(nameof(PresupuestoFile.ModifiedDate), ListSortDirection.Descending));

            // Initial folder from settings
            var paths = _settingsVm.GetCurrentPaths();
            FolderPath = paths.TryGetValue("PresupuestosFolder", out var p) ? p : string.Empty;
        }

        partial void OnFolderPathChanged(string value)
        {
            LoadFiles();
            StartWatching();
            SyncPathToSettings();
        }

        partial void OnFilterTextChanged(string value) => _filesView.Refresh();
        partial void OnFilterDateFromChanged(DateTime? value) => _filesView.Refresh();
        partial void OnFilterDateToChanged(DateTime? value) => _filesView.Refresh();

        partial void OnSelectedFileChanged(PresupuestoFile? value)
        {
            if (value != null)
                IsDetailPanelOpen = true;
        }

        private bool FilterFile(object item)
        {
            if (item is not PresupuestoFile f) return true;

            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                var text = FilterText.Trim();
                if (!f.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)
                    && !f.BudgetNumber.Contains(text, StringComparison.OrdinalIgnoreCase)
                    && !f.Company.Contains(text, StringComparison.OrdinalIgnoreCase)
                    && !f.LocationName.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (FilterDateFrom.HasValue && f.ModifiedDate.Date < FilterDateFrom.Value.Date)
                return false;

            if (FilterDateTo.HasValue && f.ModifiedDate.Date > FilterDateTo.Value.Date)
                return false;

            return true;
        }

        private void LoadFiles()
        {
            Files.Clear();
            try
            {
                if (string.IsNullOrWhiteSpace(FolderPath))
                {
                    StatusMessage = "Ingresá una ruta de carpeta.";
                    return;
                }
                if (!Directory.Exists(FolderPath))
                {
                    StatusMessage = $"La carpeta no existe: {FolderPath}";
                    return;
                }

                var paths = Directory.GetFiles(FolderPath, "*.docx", SearchOption.TopDirectoryOnly);
                foreach (var p in paths)
                {
                    try { Files.Add(PresupuestoFile.FromPath(p)); }
                    catch { /* skip unreadable files */ }
                }

                StatusMessage = $"{Files.Count} archivo(s) — {FolderPath}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error al leer la carpeta: {ex.Message}";
            }
        }

        private void StartWatching()
        {
            DisposeWatcher();
            if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath)) return;

            try
            {
                _watcher = new FileSystemWatcher(FolderPath, "*.docx")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += OnFileChanged;
                _watcher.Deleted += OnFileChanged;
                _watcher.Renamed += OnFileChanged;
                _watcher.Changed += OnFileChanged;
            }
            catch (Exception ex)
            {
                StatusMessage = $"No se pudo observar la carpeta: {ex.Message}";
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(LoadFiles));
        }

        private void SyncPathToSettings()
        {
            if (string.IsNullOrWhiteSpace(FolderPath)) return;
            if (_settingsVm.PresupuestosFolder != FolderPath)
                _settingsVm.PresupuestosFolder = FolderPath;
        }

        [RelayCommand]
        private void Refresh()
        {
            LoadFiles();
            StartWatching();
        }

        [RelayCommand]
        private void BrowseFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Seleccionar carpeta de presupuestos" };
            if (Directory.Exists(FolderPath)) dialog.InitialDirectory = FolderPath;
            if (dialog.ShowDialog() == true)
                FolderPath = dialog.FolderName;
        }

        [RelayCommand]
        private void ClearFilters()
        {
            FilterText = string.Empty;
            FilterDateFrom = null;
            FilterDateTo = null;
        }

        [RelayCommand]
        private void CloseDetail()
        {
            IsDetailPanelOpen = false;
            SelectedFile = null;
        }

        [RelayCommand]
        private void OpenFile()
        {
            if (SelectedFile == null) return;
            OpenFileOnDisk(SelectedFile.FullPath);
        }

        public void OpenFileOnDisk(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath))
                {
                    MessageBox.Show("El archivo ya no existe.", "Archivo no encontrado",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al abrir: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void ShowInExplorer()
        {
            if (SelectedFile == null) return;
            try
            {
                if (!File.Exists(SelectedFile.FullPath)) return;
                Process.Start("explorer.exe", $"/select,\"{SelectedFile.FullPath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void DeleteFile()
        {
            if (SelectedFile == null) return;

            var result = MessageBox.Show(
                $"¿Eliminar el archivo?\n\n{SelectedFile.FileName}\n\nEsta acción no se puede deshacer.",
                "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            var toDelete = SelectedFile;
            try
            {
                File.Delete(toDelete.FullPath);
                Files.Remove(toDelete);
                SelectedFile = null;
                IsDetailPanelOpen = false;
                StatusMessage = $"{Files.Count} archivo(s) — {FolderPath}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo eliminar: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DisposeWatcher()
        {
            if (_watcher == null) return;
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileChanged;
            _watcher.Deleted -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Changed -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        public void Dispose() => DisposeWatcher();
    }

    public class PresupuestoFile
    {
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string BudgetNumber { get; set; } = string.Empty;
        public string DatePart { get; set; } = string.Empty;
        public string Company { get; set; } = string.Empty;
        public string LocationName { get; set; } = string.Empty;
        public string Initials { get; set; } = string.Empty;
        public DateTime ModifiedDate { get; set; }
        public long SizeBytes { get; set; }

        public string SizeFormatted => SizeBytes switch
        {
            < 1024 => $"{SizeBytes} B",
            < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
            _ => $"{SizeBytes / (1024.0 * 1024):F1} MB"
        };

        public static PresupuestoFile FromPath(string path)
        {
            var info = new FileInfo(path);
            var nameNoExt = Path.GetFileNameWithoutExtension(info.Name);
            // Expected pattern: "{BudgetNumber}- {MMdd}- {Company}- {Location}- {Initials}"
            var parts = nameNoExt.Split(new[] { "- " }, StringSplitOptions.None);

            return new PresupuestoFile
            {
                FileName = info.Name,
                FullPath = info.FullName,
                ModifiedDate = info.LastWriteTime,
                SizeBytes = info.Length,
                BudgetNumber = parts.Length > 0 ? parts[0].Trim() : string.Empty,
                DatePart     = parts.Length > 1 ? parts[1].Trim() : string.Empty,
                Company      = parts.Length > 2 ? parts[2].Trim() : string.Empty,
                LocationName = parts.Length > 3 ? parts[3].Trim() : string.Empty,
                Initials     = parts.Length > 4 ? parts[4].Trim() : string.Empty,
            };
        }
    }
}
