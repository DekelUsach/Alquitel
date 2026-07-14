namespace Alquitel.Core.Interfaces
{
    public interface IDialogService
    {
        void ShowInfo(string title, string message);
        void ShowWarning(string title, string message);
        void ShowError(string title, string message);
        bool ShowConfirm(string title, string message);

        /// <summary>
        /// Prompt de texto de una línea. Devuelve el valor ingresado (trim) o null si
        /// el usuario canceló.
        /// </summary>
        string? ShowInput(string title, string hint, string initialValue = "");
    }
}
