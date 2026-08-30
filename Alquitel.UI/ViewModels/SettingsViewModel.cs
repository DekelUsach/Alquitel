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
        private readonly IOrderRepository _orderRepository;
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

        [ObservableProperty]
        private bool _isTestingConnection;

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

        /// <summary>0 = Vendedor, 1 = Admin, 2 = Armador (índice del combo de rol).</summary>
        [ObservableProperty]
        private int _newUserRoleIndex;

        [ObservableProperty]
        private string _newUserPassword = string.Empty;

        [ObservableProperty]
        private string _usersStatusMessage = string.Empty;

        /// <summary>Resumen de actividad del usuario seleccionado (presupuestos, montos).</summary>
        [ObservableProperty]
        private string _selectedUserStatsMessage = string.Empty;

        [ObservableProperty]
        private string _editUserName = string.Empty;

        [ObservableProperty]
        private int _editUserRoleIndex;

        public bool HasSelectedUser => SelectedUser != null;

        partial void OnSelectedUserChanged(User? value)
        {
            _ = LoadSelectedUserStatsAsync(value);
            OnPropertyChanged(nameof(HasSelectedUser));
            if (value != null)
            {
                EditUserName = value.Name;
                EditUserRoleIndex = value.Role switch
                {
                    UserRole.Admin => 1,
                    UserRole.Armador => 2,
                    _ => 0
                };
            }
            else
            {
                EditUserName = string.Empty;
                EditUserRoleIndex = 0;
            }
        }

        private async Task LoadSelectedUserStatsAsync(User? user)
        {
            if (user == null)
            {
                SelectedUserStatsMessage = string.Empty;
                return;
            }

            SelectedUserStatsMessage = $"Cargando actividad de {user.Name}…";
            try
            {
                var stats = await _orderRepository.GetUserStatsAsync(user.Id, user.Name);
                // El usuario seleccionado pudo cambiar mientras corría la consulta.
                if (SelectedUser?.Id != user.Id) return;

                SelectedUserStatsMessage = stats.OrdersCount == 0
                    ? $"{user.Name} todavía no creó presupuestos."
                    : $"{user.Name}: {stats.OrdersCount} presupuesto(s) · Total {stats.TotalAmount:C0} · " +
                      $"Último: N° {stats.LastBudgetNumber} ({stats.LastOrderDate:dd/MM/yyyy})";
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "LoadSelectedUserStatsAsync failed");
                SelectedUserStatsMessage = $"✗ No se pudo cargar la actividad: {ex.Message}";
            }
        }

        private readonly IToastService _toastService;
        private readonly Alquitel.Infrastructure.Services.DatabaseBackupService _backupService;

        public SettingsViewModel(IAppSettings appSettings, IRemoteSyncService remoteSyncService,
            IUserRepository userRepository, IOrderRepository orderRepository,
            ICurrentUserService currentUserService, IDialogService dialogService,
            ITemplateStorageService templateStorage, IToastService toastService,
            Alquitel.Infrastructure.Services.DatabaseBackupService backupService)
        {
            _backupService = backupService;
            _appSettings = appSettings;
            _remoteSyncService = remoteSyncService;
            _userRepository = userRepository;
            _orderRepository = orderRepository;
            _currentUserService = currentUserService;
            _dialogService = dialogService;
            _templateStorage = templateStorage;
            _toastService = toastService;
            LoadSettings();
            _ = LoadUsersAsync();
            _ = RefreshRemoteStatusAsync();
            _ = RefreshCloudTemplatesStatusAsync();
            RefreshBackups();
        }

        // ── Backups: listado y restauración ──────────────────────────

        public System.Collections.ObjectModel.ObservableCollection<Alquitel.Infrastructure.Services.DatabaseBackupService.BackupInfo> Backups { get; } = new();

        [ObservableProperty]
        private Alquitel.Infrastructure.Services.DatabaseBackupService.BackupInfo? _selectedBackup;

        /// <summary>El restore aplica a la base SQLite local; en modo servidor se oculta.</summary>
        public bool CanRestoreBackups => !IsRemoteConfigured;

        [RelayCommand]
        private void RefreshBackups()
        {
            Backups.Clear();
            foreach (var b in _backupService.GetAvailableBackups())
                Backups.Add(b);
        }

        [RelayCommand]
        private void RestoreSelectedBackup()
        {
            if (SelectedBackup == null)
            {
                _toastService.ShowInfo("Elegí un backup de la lista para restaurar.");
                return;
            }

            if (!_dialogService.ShowConfirm(
                "Restaurar backup",
                $"Se restaurará la base de datos al estado del {SelectedBackup.CreatedLocal:dd/MM/yyyy HH:mm}.\n\n" +
                "La base actual se guarda como copia de seguridad (Alquitel_PreRestore_*) antes de pisarla, " +
                "pero TODO lo cargado después de ese backup dejará de verse.\n\n¿Continuar?"))
                return;

            try
            {
                _backupService.RestoreBackup(SelectedBackup.FilePath);
                _dialogService.ShowInfo("Restauración programada",
                    "El backup fue validado y quedó protegido para restaurarse.\n\n" +
                    "Cerrá y volvé a abrir Alquitel: la base se reemplazará de forma segura " +
                    "antes de iniciar los módulos.");
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "RestoreSelectedBackup failed");
                _dialogService.ShowError("Error al restaurar", ex.Message);
            }
        }

        // ── Servidor ─────────────────────────────────────────────────

        [RelayCommand]
        private async Task RefreshRemoteStatusAsync()
        {
            if (IsTestingConnection) return;
            IsTestingConnection = true;
            RemoteStatusMessage = "Probando conexión…";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var status = await _remoteSyncService.TestConnectionAsync();
                sw.Stop();
                RemoteStatusMessage = status.IsConfigured
                    ? $"{status.Message}\nÚltima prueba: {DateTime.Now:HH:mm:ss} ({sw.ElapsedMilliseconds} ms)"
                    : status.Message;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "RefreshRemoteStatusAsync failed");
                RemoteStatusMessage = $"✗ Error al probar la conexión: {ex.Message}";
            }
            finally
            {
                IsTestingConnection = false;
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
                _toastService.ShowInfo("No hay servidor configurado: la app está en modo SQLite local.");
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
                _toastService.ShowSuccess("Datos locales subidos correctamente al servidor.");
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
            if (!_templateStorage.CanPublish)
            {
                _dialogService.ShowInfo("Plantillas en la nube",
                    "Este equipo no tiene la service key para publicar (la anon key de la app es de solo lectura). " +
                    "Configurá la variable de entorno ALQUITEL_Database__Supabase__ServiceKey " +
                    "o Database:Supabase:ServiceKey en appsettings.local.json en el equipo del Admin.");
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
                _toastService.ShowSuccess(
                    $"Plantilla de {kind} publicada. Todos los equipos usarán esta versión en la próxima generación.");
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
                    Role = NewUserRoleIndex switch
                    {
                        1 => UserRole.Admin,
                        2 => UserRole.Armador,
                        _ => UserRole.Vendedor
                    },
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

            // Diálogo dedicado: antes se usaba el campo "Contraseña" de la fila Agregar
            // y un campo vacío borraba la contraseña sin aviso.
            var prompt = new Views.PasswordPromptWindow(
                SelectedUser.Name,
                hasPassword: !string.IsNullOrWhiteSpace(SelectedUser.PasswordHash))
            {
                Owner = Application.Current.MainWindow
            };
            if (prompt.ShowDialog() != true) return;

            if (prompt.RemoveRequested &&
                !_dialogService.ShowConfirm("Quitar contraseña",
                    $"¿Quitar la contraseña de {SelectedUser.Name}? Podrá iniciar sesión con solo elegir su nombre."))
                return;

            try
            {
                SelectedUser.PasswordHash = prompt.RemoveRequested
                    ? null
                    : PasswordHasher.Hash(prompt.Password);
                await _userRepository.UpsertAsync(SelectedUser);
                UsersStatusMessage = prompt.RemoveRequested
                    ? $"✓ Contraseña de {SelectedUser.Name} eliminada (entra sin contraseña)."
                    : $"✓ Contraseña de {SelectedUser.Name} actualizada.";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "SetUserPasswordAsync failed");
                UsersStatusMessage = $"✗ Error al actualizar contraseña: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task DeleteUserAsync()
        {
            if (SelectedUser == null)
            {
                UsersStatusMessage = "✗ Seleccioná un usuario de la lista.";
                return;
            }
            if (SelectedUser.Id == _currentUserService.Current?.Id)
            {
                UsersStatusMessage = "✗ No podés eliminar tu propio usuario.";
                return;
            }
            if (SelectedUser.Role == UserRole.Admin &&
                Users.Count(u => u.Role == UserRole.Admin) <= 1)
            {
                UsersStatusMessage = "✗ No se puede eliminar al único Admin del sistema.";
                return;
            }

            if (!_dialogService.ShowConfirm("Eliminar usuario",
                $"¿Estás seguro de que deseas eliminar al usuario {SelectedUser.Name}?\n\nNo podrá iniciar sesión, pero sus presupuestos históricos se conservan."))
                return;

            try
            {
                await _userRepository.ArchiveAsync(SelectedUser.Id);
                UsersStatusMessage = $"✓ Usuario {SelectedUser.Name} eliminado.";
                SelectedUser = null;
                await LoadUsersAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "DeleteUserAsync failed");
                UsersStatusMessage = $"✗ Error al eliminar usuario: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task UpdateUserAsync()
        {
            if (SelectedUser == null)
            {
                UsersStatusMessage = "✗ Seleccioná un usuario de la lista.";
                return;
            }

            var name = EditUserName.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                UsersStatusMessage = "✗ El nombre no puede estar vacío.";
                return;
            }

            if (Users.Any(u => u.Id != SelectedUser.Id && string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                UsersStatusMessage = "✗ Ya existe otro usuario con ese nombre.";
                return;
            }

            var newRole = EditUserRoleIndex switch
            {
                1 => UserRole.Admin,
                2 => UserRole.Armador,
                _ => UserRole.Vendedor
            };

            // Validations for role modifications of self or last admin
            if (SelectedUser.Id == _currentUserService.Current?.Id && newRole != UserRole.Admin)
            {
                UsersStatusMessage = "✗ No podés quitarte el rol de Admin a vos mismo.";
                return;
            }

            if (SelectedUser.Role == UserRole.Admin && newRole != UserRole.Admin &&
                Users.Count(u => u.Role == UserRole.Admin) <= 1)
            {
                UsersStatusMessage = "✗ No podés cambiar el rol del único Admin del sistema.";
                return;
            }

            try
            {
                SelectedUser.Name = name;
                SelectedUser.Role = newRole;

                await _userRepository.UpsertAsync(SelectedUser);
                UsersStatusMessage = $"✓ Usuario {name} actualizado.";
                SelectedUser = null; // Clean selection after update
                await LoadUsersAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "UpdateUserAsync failed");
                UsersStatusMessage = $"✗ Error al actualizar usuario: {ex.Message}";
            }
        }

        [RelayCommand]
        private void DeselectUser()
        {
            SelectedUser = null;
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
