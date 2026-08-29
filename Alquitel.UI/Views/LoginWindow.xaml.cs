using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Alquitel.Core.Entities;
using Alquitel.Core.Helpers;
using Alquitel.Core.Interfaces;
using Alquitel.Core.Interfaces.Repositories;

namespace Alquitel.UI.Views
{
    /// <summary>
    /// Ventana de login mostrada antes de MainWindow. Selección de usuario de la lista
    /// compartida + contraseña solo si el usuario tiene una configurada.
    /// DialogResult=true únicamente con login exitoso; cancelar cierra la aplicación.
    /// </summary>
    public partial class LoginWindow : Window
    {
        private readonly ICurrentUserService _currentUserService;
        private readonly IUserRepository _userRepository;
        private readonly Alquitel.Core.Interfaces.ISessionStore _sessionStore;
        private List<User> _users = new();

        // Static: el bloqueo debe sobrevivir a que se cierre y reabra la ventana dentro
        // del mismo proceso. Un reinicio de la app sí lo limpia (costo aceptable: el
        // atacante paga el arranque completo por cada tanda de intentos).
        private static readonly Alquitel.Core.Security.LoginThrottle _throttle = new();
        private bool _loginInProgress;

        public LoginWindow(
            IUserRepository userRepository,
            ICurrentUserService currentUserService,
            Alquitel.Core.Interfaces.ISessionStore sessionStore)
        {
            _currentUserService = currentUserService;
            _userRepository = userRepository;
            _sessionStore = sessionStore;
            InitializeComponent();

            // La carga de usuarios va contra la base (que en modo servidor es Supabase
            // por internet): hacerla sincrónica en el constructor congelaba la ventana
            // durante todo el timeout de red. Se carga async al mostrarse.
            ModeText.Text = "Cargando usuarios…";
            Loaded += OnLoadedAsync;
        }

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoadedAsync;
            try
            {
                _users = await _userRepository.GetActiveAsync();
            }
            catch (Exception ex)
            {
                Alquitel.Infrastructure.AppLog.Error(ex, "No se pudo cargar la lista de usuarios para el login");
                ShowError($"No se pudo cargar la lista de usuarios:\n{ex.Message}");
            }

            UsersCombo.ItemsSource = _users;
            if (_users.Count > 0) UsersCombo.SelectedIndex = 0;

            ModeText.Text = _users.Count == 0
                ? "No hay usuarios activos. Contactá al administrador."
                : "Elegí tu nombre para registrar quién crea cada presupuesto.";
        }

        private User? SelectedUser => UsersCombo.SelectedItem as User;

        private void UsersCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var needsPassword = !string.IsNullOrWhiteSpace(SelectedUser?.PasswordHash);
            PasswordPanel.Visibility = needsPassword ? Visibility.Visible : Visibility.Collapsed;
            PasswordInput.Clear();
            HideError();
        }

        private async void PasswordInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await TryLoginAsync();
        }

        private async void Login_Click(object sender, RoutedEventArgs e) => await TryLoginAsync();

        private async Task TryLoginAsync()
        {
            if (_loginInProgress) return;

            var user = SelectedUser;
            if (user == null)
            {
                ShowError("Seleccioná un usuario.");
                return;
            }

            // Freno de fuerza bruta: sin esto, probar contraseñas contra esta ventana
            // costaba solo el tiempo de un PBKDF2 y era automatizable.
            string throttleKey = user.Id.ToString("D");
            var remaining = _throttle.GetRemainingLockout(throttleKey, DateTimeOffset.UtcNow);
            if (remaining > TimeSpan.Zero)
            {
                ShowError($"Demasiados intentos fallidos. Probá de nuevo en {FormatWait(remaining)}.");
                return;
            }

            _loginInProgress = true;
            LoginButton.IsEnabled = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(user.PasswordHash))
                {
                    string typed = PasswordInput.Password;
                    // PBKDF2 con 210.000 iteraciones congelaba la ventana ~200 ms en el
                    // hilo de UI en cada intento: se verifica en el thread pool.
                    bool ok = await Task.Run(() => PasswordHasher.Verify(typed, user.PasswordHash));
                    if (!ok)
                    {
                        var lockout = _throttle.RegisterFailure(throttleKey, DateTimeOffset.UtcNow);
                        ShowError(lockout > TimeSpan.Zero
                            ? $"Contraseña incorrecta. Esperá {FormatWait(lockout)} antes de reintentar."
                            : "Contraseña incorrecta.");
                        PasswordInput.Clear();
                        PasswordInput.Focus();
                        return;
                    }

                    // Rehash transparente si el hash guardado quedó con menos iteraciones
                    // que las vigentes (best-effort: si falla, el login igual procede).
                    if (PasswordHasher.NeedsRehash(user.PasswordHash))
                    {
                        try
                        {
                            user.PasswordHash = await Task.Run(() => PasswordHasher.Hash(typed));
                            await _userRepository.UpsertAsync(user);
                        }
                        catch (Exception ex)
                        {
                            Alquitel.Infrastructure.AppLog.Warning(ex, "No se pudo re-hashear la contraseña de {User}", user.Name);
                        }
                    }
                }

                // Un Admin sin contraseña tiene acceso total a datos de facturación con solo
                // elegir su nombre. Se obliga a definir una en el primer login; cancelar no entra.
                if (user.Role == UserRole.Admin && string.IsNullOrWhiteSpace(user.PasswordHash))
                {
                    var prompt = new PasswordPromptWindow(user.Name, hasPassword: false) { Owner = this };
                    if (prompt.ShowDialog() != true || prompt.RemoveRequested)
                    {
                        ShowError("Un usuario Admin debe tener contraseña para poder ingresar.");
                        return;
                    }

                    var weakness = PasswordHasher.ValidateNewPassword(prompt.Password);
                    if (weakness != null)
                    {
                        ShowError(weakness);
                        return;
                    }

                    user.PasswordHash = await Task.Run(() => PasswordHasher.Hash(prompt.Password));
                    try
                    {
                        await _userRepository.UpsertAsync(user);
                    }
                    catch (Exception ex)
                    {
                        Alquitel.Infrastructure.AppLog.Error(ex, "No se pudo guardar la contraseña inicial del Admin");
                        ShowError($"No se pudo guardar la contraseña:\n{ex.Message}");
                        user.PasswordHash = null;
                        return;
                    }
                }

                _throttle.Reset(throttleKey);
                _currentUserService.SetCurrentUser(user);
                _sessionStore.Save(user.Id, PasswordHasher.Fingerprint(user.PasswordHash));
                DialogResult = true;
            }
            finally
            {
                _loginInProgress = false;
                LoginButton.IsEnabled = true;
            }
        }

        private static string FormatWait(TimeSpan wait)
        {
            if (wait.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} s";
            return $"{(int)Math.Ceiling(wait.TotalMinutes)} min";
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
    }
}
