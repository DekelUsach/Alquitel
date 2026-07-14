using CommunityToolkit.Mvvm.ComponentModel;

namespace Alquitel.Mobile.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Alert simple sobre la página visible (errores y confirmaciones).</summary>
    protected static Task ShowAlertAsync(string title, string message) =>
        Shell.Current.DisplayAlert(title, message, "OK");

    protected static Task<bool> ConfirmAsync(string title, string message) =>
        Shell.Current.DisplayAlert(title, message, "Sí", "Cancelar");

    protected static string DescribeDbError(Exception ex) =>
        ex switch
        {
            InvalidOperationException ioe when ioe.Message.Contains("no está configurada") => ioe.Message,
            _ => $"No se pudo conectar a la base de datos. Verificá tu conexión a internet.\n\nDetalle: {ex.GetBaseException().Message}",
        };
}
