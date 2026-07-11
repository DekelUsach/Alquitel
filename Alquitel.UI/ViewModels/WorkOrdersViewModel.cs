using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Infrastructure;
using Alquitel.Infrastructure.Services;
using Alquitel.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Data;

namespace Alquitel.UI.ViewModels
{
    /// <summary>
    /// Vista simple de Órdenes de Trabajo pensada para el rol Armador (depósito):
    /// lista los .docx de la carpeta de OT y los previsualiza como texto plano dentro
    /// de la app, sin necesidad de abrir Word. También la usa el Admin.
    /// </summary>
    public partial class WorkOrdersViewModel : ObservableObject, IAsyncInitialization, IDisposable
    {
        private readonly IAppSettings _appSettings;
        private readonly IDialogService _dialogService;
        private readonly IDispatcher _dispatcher;
        private readonly ICollectionView _filesView;
        private FileSystemWatcher? _watcher;

        [ObservableProperty]
        private string _filterText = string.Empty;

        [ObservableProperty]
        private WorkOrderFile? _selectedFile;

        [ObservableProperty]
        private string _previewText = string.Empty;

        [ObservableProperty]
        private bool _isLoadingPreview;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public ObservableCollection<WorkOrderFile> Files { get; } = new();
        public ICollectionView FilesView => _filesView;

        public string FolderPath => _appSettings.OtFolder;

        public WorkOrdersViewModel(IAppSettings appSettings, IDialogService dialogService, IDispatcher dispatcher)
        {
            _appSettings = appSettings;
            _dialogService = dialogService;
            _dispatcher = dispatcher;

            _filesView = CollectionViewSource.GetDefaultView(Files);
            _filesView.Filter = FilterFile;
            _filesView.SortDescriptions.Add(
                new SortDescription(nameof(WorkOrderFile.ModifiedDate), ListSortDirection.Descending));
        }

        partial void OnFilterTextChanged(string value) => _filesView.Refresh();

        partial void OnSelectedFileChanged(WorkOrderFile? value) => _ = LoadPreviewAsync(value);

        private bool FilterFile(object item)
        {
            if (item is not WorkOrderFile f) return true;
            return string.IsNullOrWhiteSpace(FilterText)
                || f.FileName.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public async Task InitializeAsync()
        {
            Files.Clear();
            PreviewText = string.Empty;
            try
            {
                var folder = FolderPath;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    StatusMessage = $"La carpeta de OT no existe: {folder}. Pedile al administrador que la configure.";
                    return;
                }

                var files = await Task.Run(() =>
                {
                    var list = new List<WorkOrderFile>();
                    foreach (var p in Directory.GetFiles(folder, "*.docx", SearchOption.TopDirectoryOnly))
                    {
                        try { list.Add(WorkOrderFile.FromPath(p)); }
                        catch (Exception ex) { AppLog.Warning(ex, "Skipping unreadable OT file {Path}", p); }
                    }
                    return list;
                });

                foreach (var f in files) Files.Add(f);
                StatusMessage = $"{Files.Count} orden(es) de trabajo — {folder}";
                StartWatching();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "WorkOrders InitializeAsync failed for {Folder}", FolderPath);
                StatusMessage = $"Error al leer la carpeta: {ex.Message}";
            }
        }

        private async Task LoadPreviewAsync(WorkOrderFile? file)
        {
            if (file == null)
            {
                PreviewText = string.Empty;
                return;
            }

            IsLoadingPreview = true;
            try
            {
                var text = await Task.Run(() => DocxTextExtractor.ExtractPlainText(file.FullPath));
                // La selección pudo cambiar mientras se extraía el texto.
                if (SelectedFile?.FullPath != file.FullPath) return;
                PreviewText = string.IsNullOrWhiteSpace(text)
                    ? "(El documento no tiene texto para previsualizar. Usá \"Abrir en Word\".)"
                    : text;
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "LoadPreviewAsync failed for {Path}", file.FullPath);
                PreviewText = $"No se pudo generar la vista previa: {ex.Message}\n\nUsá \"Abrir en Word\".";
            }
            finally
            {
                IsLoadingPreview = false;
            }
        }

        [RelayCommand]
        private void Refresh() => _ = InitializeAsync();

        [RelayCommand]
        private void OpenFile()
        {
            if (SelectedFile == null) return;
            try
            {
                if (!PathValidator.IsDocxWithinRoot(SelectedFile.FullPath, FolderPath))
                {
                    AppLog.Warning("Rejected OT OpenFile — path outside folder: {Path}", SelectedFile.FullPath);
                    _dialogService.ShowWarning("Acceso denegado", "Ruta inválida o fuera de la carpeta de OT.");
                    return;
                }
                if (!File.Exists(SelectedFile.FullPath))
                {
                    _dialogService.ShowWarning("Archivo no encontrado", "El archivo ya no existe.");
                    return;
                }
                Process.Start(new ProcessStartInfo(SelectedFile.FullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "OT OpenFile failed for {Path}", SelectedFile.FullPath);
                _dialogService.ShowError("Error", $"Error al abrir: {ex.Message}");
            }
        }

        private void StartWatching()
        {
            if (_watcher != null && string.Equals(_watcher.Path, FolderPath, StringComparison.OrdinalIgnoreCase))
                return;

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
                AppLog.Warning(ex, "OT FileSystemWatcher init failed for {Folder}", FolderPath);
            }
        }

        private System.Threading.Timer? _debounceTimer;
        private const int DebounceMs = 300;

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            var timer = _debounceTimer;
            if (timer != null)
            {
                timer.Change(DebounceMs, System.Threading.Timeout.Infinite);
                return;
            }

            _debounceTimer = new System.Threading.Timer(
                _ => _dispatcher.InvokeAsync(() => _ = InitializeAsync()),
                null, DebounceMs, System.Threading.Timeout.Infinite);
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

        public void Dispose()
        {
            DisposeWatcher();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    public class WorkOrderFile
    {
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public DateTime ModifiedDate { get; set; }
        public long SizeBytes { get; set; }

        public string ModifiedFormatted => ModifiedDate.ToString("dd/MM/yyyy HH:mm");

        public static WorkOrderFile FromPath(string path)
        {
            var info = new FileInfo(path);
            return new WorkOrderFile
            {
                FileName = info.Name,
                FullPath = info.FullName,
                ModifiedDate = info.LastWriteTime,
                SizeBytes = info.Length,
            };
        }
    }
}
