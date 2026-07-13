using System;

namespace Alquitel.Core.Interfaces
{
    /// <summary>
    /// Notificaciones no bloqueantes (toast/snackbar) para éxitos y avisos menores.
    /// Se auto-descartan a los pocos segundos y admiten una acción opcional
    /// ("Abrir carpeta", "Deshacer"). Los MessageBox quedan reservados para errores
    /// y confirmaciones destructivas (ver IDialogService).
    /// </summary>
    public interface IToastService
    {
        /// <summary>Toast de éxito auto-descartable.</summary>
        void ShowSuccess(string message);

        /// <summary>Toast de éxito con una acción opcional (ej: "Abrir carpeta").</summary>
        void ShowSuccess(string message, string actionLabel, Action action);

        /// <summary>Toast informativo neutro (avisos menores no críticos).</summary>
        void ShowInfo(string message);
    }
}
