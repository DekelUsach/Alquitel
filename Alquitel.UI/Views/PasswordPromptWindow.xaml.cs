using System.Windows;

namespace Alquitel.UI.Views
{
    /// <summary>
    /// Diálogo modal para establecer o quitar la contraseña de un usuario.
    /// DialogResult=true cuando hubo acción; ver <see cref="RemoveRequested"/> y
    /// <see cref="Password"/> para saber cuál.
    /// </summary>
    public partial class PasswordPromptWindow : Window
    {
        /// <summary>Contraseña ingresada (solo válida si RemoveRequested es false).</summary>
        public string Password { get; private set; } = string.Empty;

        /// <summary>True si el usuario pidió quitar la contraseña.</summary>
        public bool RemoveRequested { get; private set; }

        public PasswordPromptWindow(string userName, bool hasPassword)
        {
            InitializeComponent();
            TitleText.Text = $"Contraseña de {userName}";
            RemoveButton.Visibility = hasPassword ? Visibility.Visible : Visibility.Collapsed;
            Loaded += (_, _) => PasswordInput.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PasswordInput.Password))
            {
                ShowError("Ingresá una contraseña. Para quitarla usá el botón \"Quitar contraseña\".");
                return;
            }
            if (PasswordInput.Password != ConfirmInput.Password)
            {
                ShowError("Las contraseñas no coinciden.");
                ConfirmInput.Clear();
                ConfirmInput.Focus();
                return;
            }

            Password = PasswordInput.Password;
            RemoveRequested = false;
            DialogResult = true;
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            RemoveRequested = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
