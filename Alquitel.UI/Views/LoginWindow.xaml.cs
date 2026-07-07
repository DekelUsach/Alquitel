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
        private List<User> _users = new();

        public LoginWindow(IUserRepository userRepository, ICurrentUserService currentUserService)
        {
            _currentUserService = currentUserService;
            InitializeComponent();

            try
            {
                // Bloqueante a propósito: sin usuarios no hay aplicación que mostrar.
                // Se usa Task.Run para evitar un deadlock en el hilo de UI de WPF al llamar a GetResult().
                _users = Task.Run(() => userRepository.GetActiveAsync()).GetAwaiter().GetResult();
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

        private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) TryLogin();
        }

        private void Login_Click(object sender, RoutedEventArgs e) => TryLogin();

        private void TryLogin()
        {
            var user = SelectedUser;
            if (user == null)
            {
                ShowError("Seleccioná un usuario.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(user.PasswordHash) &&
                !PasswordHasher.Verify(PasswordInput.Password, user.PasswordHash))
            {
                ShowError("Contraseña incorrecta.");
                PasswordInput.Clear();
                PasswordInput.Focus();
                return;
            }

            _currentUserService.SetCurrentUser(user);
            DialogResult = true;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
    }
}
