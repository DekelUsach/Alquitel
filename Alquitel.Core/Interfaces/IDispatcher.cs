using System;

namespace Alquitel.Core.Interfaces
{
    public interface IDispatcher
    {
        void InvokeAsync(Action action);
    }
}
