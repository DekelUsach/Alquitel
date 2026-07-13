using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Alquitel.Core.Interfaces;
using CommunityToolkit.Mvvm.Input;

namespace Alquitel.UI.Services
{
    /// <summary>Toast individual mostrado en el host de MainWindow.</summary>
    public sealed partial class ToastItem
    {
        private readonly Action<ToastItem> _dismiss;

        public string Message { get; }
        public string? ActionLabel { get; }
        public bool HasAction => !string.IsNullOrEmpty(ActionLabel);
        /// <summary>Glyph de Segoe (✓ éxito / ⓘ info) para el ícono del toast.</summary>
        public string IconGlyph { get; }
        public bool IsSuccess { get; }

        private readonly Action? _action;

        public ToastItem(string message, string? actionLabel, Action? action, bool isSuccess, Action<ToastItem> dismiss)
        {
            Message = message;
            ActionLabel = actionLabel;
            _action = action;
            IsSuccess = isSuccess;
            IconGlyph = isSuccess ? "" : ""; // CheckMark / Info
            _dismiss = dismiss;
        }

        [RelayCommand]
        private void RunAction()
        {
            _action?.Invoke();
            _dismiss(this);
        }

        [RelayCommand]
        private void Dismiss() => _dismiss(this);
    }

    /// <summary>
    /// Implementación WPF de <see cref="IToastService"/>: mantiene la cola visible
    /// (máx. 3 simultáneos) y auto-descarta cada toast a los 4,5 segundos. Todas las
    /// mutaciones de la colección se despachan al hilo de UI.
    /// </summary>
    public class ToastService : IToastService
    {
        private const int MaxVisible = 3;
        private static readonly TimeSpan AutoDismissAfter = TimeSpan.FromSeconds(4.5);

        private readonly IDispatcher _dispatcher;

        /// <summary>Toasts visibles; MainWindow bindea este ItemsSource.</summary>
        public ObservableCollection<ToastItem> Items { get; } = new();

        public ToastService(IDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void ShowSuccess(string message) => Show(message, null, null, isSuccess: true);

        public void ShowSuccess(string message, string actionLabel, Action action)
            => Show(message, actionLabel, action, isSuccess: true);

        public void ShowInfo(string message) => Show(message, null, null, isSuccess: false);

        private void Show(string message, string? actionLabel, Action? action, bool isSuccess)
        {
            _dispatcher.InvokeAsync(() =>
            {
                var toast = new ToastItem(message, actionLabel, action, isSuccess, Remove);
                while (Items.Count >= MaxVisible) Items.RemoveAt(0);
                Items.Add(toast);

                var timer = new DispatcherTimer { Interval = AutoDismissAfter };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Remove(toast);
                };
                timer.Start();
            });
        }

        private void Remove(ToastItem toast)
        {
            _dispatcher.InvokeAsync(() => Items.Remove(toast));
        }
    }
}
