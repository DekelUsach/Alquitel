using Alquitel.Core.Interfaces;
using System;
using System.Windows;

namespace Alquitel.UI.Services
{
    public class WpfDispatcher : IDispatcher
    {
        public void InvokeAsync(Action action)
        {
            Application.Current?.Dispatcher.BeginInvoke(action);
        }
    }
}
