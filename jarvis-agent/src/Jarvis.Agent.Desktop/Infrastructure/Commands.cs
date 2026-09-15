using System.Windows.Input;
namespace Jarvis.Agent.Desktop.Infrastructure;
public sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
public sealed class AsyncCommand(Func<Task> action, Action<Exception> onError) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;
    public async void Execute(object? parameter)
    {
        if (_running) return; _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await action(); } catch (Exception ex) { onError(ex); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
