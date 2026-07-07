using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alquitel.Infrastructure;
using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Core.Interfaces;
using Alquitel.Core.Interfaces.Repositories;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Alquitel.UI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IAppSettings _appSettings;
        private readonly IRemoteSyncService _remoteSyncService;
        private readonly IUserRepository _userRepository;
        private readonly ICurrentUserService _currentUserService;
        private readonly IDialogService _dialogService;
        private readonly ITemplateStorageService _templateStorage;

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
        private bool _exportPdf;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        // ── Servidor remoto (Supabase/PostgreSQL) ────────────────────
        [ObservableProperty]
        private string _remoteStatusMessage = string.Empty;

        [ObservableProperty]
        private bool _isPushingData;

        public bool IsRemoteConfigured => _remoteSyncService.IsRemoteConfigured;

        // ── Plantillas en la nube (solo Admin) ───────────────────────
        /// <summary>Gate de visibilidad de la tarjeta de publicación de plantillas.</summary>
        public bool IsAdmin => _currentUserService.IsAdmin;

        public bool IsCloudTemplatesConfigured => _templateStorage.IsConfigured;

        [ObservableProperty]
        private string _cloudTemplatesStatusMessage = string.Empty;

        [ObservableProperty]
        private bool _isPublishingTemplate;

        // ── Gestión de usuarios (solo Admin) ─────────────────────────
        public ObservableCollection<User> Users { get; } = new();

        [ObservableProperty]
        private User? _selectedUser;

        [ObservableProperty]
        private string _newUserName = string.Empty;

        /// <summary>0 = Vendedor, 1 = Admin (índice del combo de rol).</summary>
        [ObservableProperty]
        private int _newUserRoleIndex;

        [ObservableProperty]
        private string _newUserPassword = string.Empty;

        [ObservableProperty]
        private string _usersStatusMessage = string.Empty;

        public SettingsViewModel(IAppSettings appSettings, IRemoteSyncService remoteSyncService,
            IUserRepository userRepository, ICurrentUserService currentUserService, IDialogService dialogService,
            ITemplateStorageService templateStorage)
        {
            _appSettings = appSettings;
            _remoteSyncService = remoteSyncService;
            _userRepository = userRepository;
            _currentUserService = currentUserService;
            _dialogService = dialogService;
            _templateStorage = templateStorage;
            LoadSettings();
            _ = LoadUsersAsync();
            _ = RefreshRemoteStatusAsync();
            _ = RefreshCloudTemplatesStatusAsync();
        }

        // ── Servidor ─────────────────────────────────────────────────

        [RelayCommand]
        private async Task RefreshRemoteStatusAsync()
        {
            try
            {
                var status = await _remoteSyncService.TestConnectionAsync();
                RemoteStatusMessage = status.Message;
            }
            catch (Exception ex)
            {
                RemoteStatusMessage = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Carga inicial one-shot: sube toda la base SQLite local de esta máquina al
        /// servidor compartido. Pensado para ejecutarse una vez desde la máquina que
        /// tenía el histórico antes de pasar al modo servidor.
        /// </summary>
        [RelayCommand]
        private async Task PushLocalDataAsync()
        {
            if (!IsRemoteConfigured)
            {
                _dialogService.ShowInfo("Servidor", "No hay servidor configurado: la app está en modo SQLite local.");
                return;
            }

            if (!_dialogService.ShowConfirm(
                "Subir datos locales",
                "Se subirán todos los clientes, productos, ubicaciones y presupuestos de la base " +
                "local de esta máquina al servidor compartido.\n\nLos registros con el mismo Id se " +
                "sobrescriben en el servidor. ¿Continuar?"))
                return;

            IsPushingData = true;
            try
            {
                await _remoteSyncService.PushPendingChangesAsync();
                _dialogService.ShowInfo("Servidor", "Datos locales subidos correctamente al servidor.");
                await RefreshRemoteStatusAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "PushLocalDataAsync failed");
                _dialogService.ShowError("Error al subir datos", ex.Message);
            }
            finally
            {
                IsPushingData = false;
            }
        }

        // ── Plantillas en la nube ────────────────────────────────────

        [RelayCommand]
        private async Task RefreshCloudTemplatesStatusAsync()
        {
            if (!_templateStorage.IsConfigured)
            {
                CloudTemplatesStatusMessage = "Sin configurar: falta Url/AnonKey de Supabase en appsettings.json.";
                return;
            }

            try
            {
                var lines = new List<string>();
                foreach (var kind in new[] { TemplateKind.Presupuesto, TemplateKind.OF, TemplateKind.OT })
                {
                    var status = await _templateStorage.GetStatusAsync(kind);
                    lines.Add(status.Exists
                        ? $"• {kind}: publicada ({status.UpdatedAt:dd/MM/yyyy HH:mm}, {status.SizeBytes / 1024} KB)"
                        : $"• {kind}: sin plantilla publicada (se usa la ruta local)");
                }
                CloudTemplatesStatusMessage = string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                CloudTemplatesStatusMessage = $"✗ Error al consultar el servidor: {ex.Message}";
            }
        }

        [RelayCommand]
        private Task PublishPresupuestoTemplateAsync() => PublishTemplateAsync(TemplateKind.Presupuesto);

        [RelayCommand]
        private Task PublishOfTemplateAsync() => PublishTemplateAsync(TemplateKind.OF);

        [RelayCommand]
        private Task PublishOtTemplateAsync() => PublishTemplateAsync(TemplateKind.OT);

        private async Task PublishTemplateAsync(TemplateKind kind)
        {
            if (!_currentUserService.IsAdmin)
            {
                _dialogService.ShowWarning("Permisos", "Solo un Admin puede publicar plantillas.");
                return;
            }
            if (!_templateStorage.IsConfigured)
            {
                _dialogService.ShowInfo("Plantillas en la nube",
                    "No hay servidor configurado: completá Url y AnonKey de Supabase en appsettings.json.");
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Seleccionar plantilla de {kind} a publicar",
                Filter = "Documentos Word (*.docx)|*.docx"
            };
            if (dialog.ShowDialog() != true) return;

            if (!_dialogService.ShowConfirm(
                "Publicar plantilla",
                $"Se publicará \"{Path.GetFileName(dialog.FileName)}\" como plantilla oficial de {kind} " +
                "para TODOS los puestos de trabajo.\n\nLa versión anterior en el servidor se reemplaza. ¿Continuar?"))
                return;

            IsPublishingTemplate = true;
            try
            {
                await _templateStorage.PublishTemplateAsync(kind, dialog.FileName);
                _dialogService.ShowInfo("Plantillas en la nube",
                    $"Plantilla de {kind} publicada correctamente. Todos los equipos usarán esta versión en la próxima generación.");
                await RefreshCloudTemplatesStatusAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "PublishTemplateAsync({Kind}) failed", kind);
                _dialogService.ShowError("Error al publicar plantilla", ex.Message);
            }
            finally
            {
                IsPublishingTemplate = false;
            }
        }

        // ── Usuarios ─────────────────────────────────────────────────

        private async Task LoadUsersAsync()
        {
            try
            {
                var users = await _userRepository.GetActiveAsync();
                Users.Clear();
                foreach (var u in users) Users.Add(u);
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "LoadUsersAsync failed");
                UsersStatusMessage = $"✗ Error al cargar usuarios: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task AddUserAsync()
        {
            var name = NewUserName.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                UsersStatusMessage = "✗ Ingresá un nombre de usuario.";
                return;
            }
            if (Users.Any(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                UsersStatusMessage = "✗ Ya existe un usuario con ese nombre.";
                return;
            }

            try
            {
                var user = new User
                {
                    Name = name,
                    Role = NewUserRoleIndex == 1 ? UserRole.Admin : UserRole.Vendedor,
                    PasswordHash = string.IsNullOrWhiteSpace(NewUserPassword)
                        ? null
                        : PasswordHasher.Hash(NewUserPassword)
                };
                await _userRepository.UpsertAsync(user);
                NewUserName = string.Empty;
                NewUserPassword = string.Empty;
                NewUserRoleIndex = 0;
                UsersStatusMessage = $"✓ Usuario {name} creado.";
                await LoadUsersAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "AddUserAsync failed");
                UsersStatusMessage = $"✗ Error al crear usuario: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task SetUserPasswordAsync()
        {
            if (SelectedUser == null)
            {
                UsersStatusMessage = "✗ Seleccioná un usuario de la lista.";
                return;
            }

            try
            {
                SelectedUser.PasswordHash = string.IsNullOrWhiteSpace(NewUserPassword)
                    ? null
                    : PasswordHasher.Hash(NewUserPassword);
                await _userRepository.UpsertAsync(SelectedUser);
                UsersStatusMessage = string.IsNullOrWhiteSpace(NewUserPassword)
                    ? $"✓ Contraseña de {SelectedUser.Name} eliminada (entra sin contraseña)."
                    : $"✓ Contraseña de {SelectedUser.Name} actualizada.";
                NewUserPassword = string.Empty;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "SetUserPasswordAsync failed");
                UsersStatusMessage = $"✗ Error al actualizar contraseña: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task ArchiveUserAsync()
        {
            if (SelectedUser == null)
            {
                UsersStatusMessage = "✗ Seleccioná un usuario de la lista.";
                return;
            }
            if (SelectedUser.Id == _currentUserService.Current?.Id)
            {
                UsersStatusMessage = "✗ No podés archivar tu propio usuario.";
                return;
            }
            if (SelectedUser.Role == UserRole.Admin &&
                Users.Count(u => u.Role == UserRole.Admin) <= 1)
            {
                UsersStatusMessage = "✗ No se puede archivar al único Admin del sistema.";
                return;
            }

            if (!_dialogService.ShowConfirm("Archivar usuario",
                $"¿Archivar al usuario {SelectedUser.Name}? No podrá iniciar sesión, " +
                "pero sus presupuestos históricos se conservan."))
                return;

            try
            {
                await _userRepository.ArchiveAsync(SelectedUser.Id);
                UsersStatusMessage = $"✓ Usuario {SelectedUser.Name} archivado.";
                SelectedUser = null;
                await LoadUsersAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "ArchiveUserAsync failed");
                UsersStatusMessage = $"✗ Error al archivar: {ex.Message}";
            }
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
                _appSettings.ExportPdf = ExportPdf;
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
            ExportPdf = _appSettings.ExportPdf;
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
