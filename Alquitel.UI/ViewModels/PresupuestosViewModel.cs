using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Infrastructure;
using Alquitel.Core.Helpers;
using Alquitel.Core.Interfaces;
using Alquitel.Core.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
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
    public partial class PresupuestosViewModel : ObservableObject, IDisposable, IAsyncInitialization
    {
        private readonly IAppSettings _appSettings;
        private readonly IDialogService _dialogService;
        private readonly IDispatcher _dispatcher;
        private readonly IOrderRepository _orderRepository;
        private readonly IProductRepository _productRepository;
        private readonly ICurrentUserService _currentUserService;
        private readonly INavigationService _navigationService;
        private readonly IServiceProvider _serviceProvider;
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

        [ObservableProperty]
        private bool _isCreatingVersion;

        public ObservableCollection<PresupuestoFile> Files { get; } = new();
        public ICollectionView FilesView => _filesView;

        public PresupuestosViewModel(IAppSettings appSettings, IDialogService dialogService, IDispatcher dispatcher,
            IOrderRepository orderRepository, IProductRepository productRepository,
            ICurrentUserService currentUserService,
            INavigationService navigationService, IServiceProvider serviceProvider)
        {
            _appSettings = appSettings;
            _dialogService = dialogService;
            _dispatcher = dispatcher;
            _orderRepository = orderRepository;
            _productRepository = productRepository;
            _currentUserService = currentUserService;
            _navigationService = navigationService;
            _serviceProvider = serviceProvider;

            _filesView = CollectionViewSource.GetDefaultView(Files);
            _filesView.Filter = FilterFile;
            _filesView.SortDescriptions.Add(
                new SortDescription(nameof(PresupuestoFile.ModifiedDate), ListSortDirection.Descending));

            // Initial folder from settings. Write the backing field directly:
            // setting the property would fire OnFolderPathChanged → InitializeAsync,
            // duplicating the scan the NavigationService triggers right after.
            _folderPath = _appSettings.PresupuestosFolder;
        }

        partial void OnFolderPathChanged(string value)
        {
            _ = InitializeAsync();
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
                    && !f.LocationName.Contains(text, StringComparison.OrdinalIgnoreCase)
                    && !f.Initials.Contains(text, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (FilterDateFrom.HasValue && f.ModifiedDate.Date < FilterDateFrom.Value.Date)
                return false;

            if (FilterDateTo.HasValue && f.ModifiedDate.Date > FilterDateTo.Value.Date)
                return false;

            return true;
        }

        public async Task InitializeAsync()
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

                // Single background hop for the whole scan — one Task.Run per file
                // serialized the I/O and thrashed the thread pool on large folders.
                var folder = FolderPath;
                var parsed = await Task.Run(() =>
                {
                    var list = new List<PresupuestoFile>();
                    foreach (var p in Directory.GetFiles(folder, "*.docx", SearchOption.TopDirectoryOnly))
                    {
                        try { list.Add(PresupuestoFile.FromPath(p)); }
                        catch (Exception ex) { AppLog.Warning(ex, "Skipping unreadable file {Path}", p); }
                    }
                    return list;
                });

                foreach (var f in parsed) Files.Add(f);

                StatusMessage = $"{Files.Count} archivo(s) — {FolderPath}";
                StartWatching();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "InitializeAsync failed for {Folder}", FolderPath);
                StatusMessage = $"Error al leer la carpeta: {ex.Message}";
            }
        }

        private void StartWatching()
        {
            // Reuse the live watcher when the folder didn't change — recreating it on
            // every refresh caused watcher churn on each file event.
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
                AppLog.Warning(ex, "FileSystemWatcher init failed for {Folder}", FolderPath);
                StatusMessage = $"No se pudo observar la carpeta: {ex.Message}";
            }
        }

        private System.Threading.Timer? _debounceTimer;
        private const int DebounceMs = 300;

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Trailing-edge debounce: every event pushes the reload 300ms into the
            // future, so a burst of writes triggers exactly one refresh at the end.
            // (The previous leading-edge version dropped events inside the window.)
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

        private void SyncPathToSettings()
        {
            if (string.IsNullOrWhiteSpace(FolderPath)) return;
            if (_appSettings.PresupuestosFolder != FolderPath)
            {
                _appSettings.PresupuestosFolder = FolderPath;
                _appSettings.SaveSettings();
            }
        }

        [RelayCommand]
        private void Refresh()
        {
            _ = InitializeAsync();
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
                if (!IsAllowedDocxPath(fullPath))
                {
                    AppLog.Warning("Rejected OpenFile — path outside watch folder: {Path}", fullPath);
                    _dialogService.ShowWarning("Acceso denegado", "Ruta inválida o fuera de la carpeta supervisada.");
                    return;
                }
                if (!File.Exists(fullPath))
                {
                    _dialogService.ShowWarning("Archivo no encontrado", "El archivo ya no existe.");
                    return;
                }
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "OpenFileOnDisk failed for {Path}", fullPath);
                _dialogService.ShowError("Error", $"Error al abrir: {ex.Message}");
            }
        }

        private bool IsAllowedDocxPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath)) return false;
            return PathValidator.IsDocxWithinRoot(fullPath, FolderPath);
        }

        /// <summary>
        /// Ramificación desde el historial: busca la orden del archivo seleccionado en la
        /// base, abre el editor de versiones (BranchEditorWindow) y, al confirmar, genera
        /// el documento de la rama nueva directamente.
        /// </summary>
        [RelayCommand]
        private async Task CreateVersionAsync()
        {
            if (SelectedFile == null) return;

            try
            {
                // El nombre de archivo guarda "31294(2)"; la base guarda "31294/2".
                string dbNumber = BudgetNumberHelper.FromFileNameForm(SelectedFile.BudgetNumber);
                var order = await _orderRepository.GetByBudgetNumberAsync(dbNumber)
                         ?? await _orderRepository.GetByBudgetNumberAsync(SelectedFile.BudgetNumber);

                if (order == null)
                {
                    _dialogService.ShowWarning("Nueva versión",
                        $"El presupuesto {SelectedFile.BudgetNumber} no está registrado en la base de datos " +
                        "(probablemente el archivo se generó fuera de la app o con otro número). " +
                        "Solo se pueden ramificar presupuestos registrados.");
                    return;
                }

                var numbers = await _orderRepository.GetAllBudgetNumbersAsync();
                string newNumber = BudgetNumberHelper.NextVersion(order.BudgetNumber, numbers);

                // Catálogo activo para poder agregar productos nuevos a la versión.
                var catalog = await _productRepository.GetAllAsync();

                var editorVm = new BranchEditorViewModel(order, newNumber, catalog);
                var editor = new Views.BranchEditorWindow(editorVm)
                {
                    Owner = Application.Current.MainWindow
                };

                if (editor.ShowDialog() != true) return;

                var branch = editorVm.BuildBranchOrder(_currentUserService.Current);

                // Genera el documento de la nueva versión directamente, sin pasar por
                // la pantalla del armador de presupuestos.
                IsCreatingVersion = true;
                try
                {
                    var builder = _serviceProvider.GetRequiredService<BudgetBuilderViewModel>();
                    builder.LoadBranchOrder(branch);
                    await builder.GenerateBudgetCommand.ExecuteAsync(null);
                }
                finally
                {
                    IsCreatingVersion = false;
                }
                IsDetailPanelOpen = false;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "CreateVersionAsync failed for {File}", SelectedFile?.FileName);
                _dialogService.ShowError("Error al crear versión", ex.Message);
            }
        }

        [RelayCommand]
        private void ShowInExplorer()
        {
            if (SelectedFile == null) return;
            if (!IsAllowedDocxPath(SelectedFile.FullPath))
            {
                AppLog.Warning("Rejected ShowInExplorer — invalid path: {Path}", SelectedFile.FullPath);
                return;
            }
            try
            {
                if (!File.Exists(SelectedFile.FullPath)) return;
                var psi = new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"/select,\"{SelectedFile.FullPath}\"",
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "ShowInExplorer failed for {Path}", SelectedFile.FullPath);
                _dialogService.ShowError("Error", $"Error: {ex.Message}");
            }
        }

        [RelayCommand]
        private void DeleteFile()
        {
            if (SelectedFile == null) return;
            if (!IsAllowedDocxPath(SelectedFile.FullPath))
            {
                AppLog.Warning("Rejected DeleteFile — invalid path: {Path}", SelectedFile.FullPath);
                _dialogService.ShowWarning("Acceso denegado", "Ruta inválida o fuera de la carpeta supervisada.");
                return;
            }

            var result = _dialogService.ShowConfirm(
                "Confirmar eliminación",
                $"¿Eliminar el archivo?\n\n{SelectedFile.FileName}\n\nEsta acción no se puede deshacer.");

            if (!result) return;

            var toDelete = SelectedFile;
            try
            {
                File.Delete(toDelete.FullPath);
                Files.Remove(toDelete);
                SelectedFile = null;
                IsDetailPanelOpen = false;
                StatusMessage = $"{Files.Count} archivo(s) — {FolderPath}";
                AppLog.Information("File deleted: {Path}", toDelete.FullPath);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "DeleteFile failed for {Path}", toDelete.FullPath);
                _dialogService.ShowError("Error", $"No se pudo eliminar: {ex.Message}");
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

        public void Dispose()
        {
            DisposeWatcher();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
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
