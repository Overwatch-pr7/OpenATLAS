using System;
using System.Windows.Input;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>
    /// Standard WPF ICommand implementation that delegates execution and can-execute
    /// logic to Action and Predicate delegates.
    /// Enables declarative MVVM commanding from XAML buttons without code-behind coupling.
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        /// <summary>
        /// Initializes a new command that can always execute.
        /// </summary>
        /// <param name="execute">The delegate to execute when invoked.</param>
        public RelayCommand(Action<object?> execute) : this(execute, null)
        {
        }

        /// <summary>
        /// Convenience overload accepting parameterless Action.
        /// </summary>
        /// <param name="execute">The parameterless delegate to execute.</param>
        public RelayCommand(Action execute) : this(_ => execute(), null)
        {
        }

        /// <summary>
        /// Convenience overload accepting parameterless Action and Func<bool> canExecute.
        /// </summary>
        public RelayCommand(Action execute, Func<bool>? canExecute)
            : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
        {
        }

        /// <summary>
        /// Initializes a new command with conditional execution.
        /// </summary>
        /// <param name="execute">The delegate to execute when invoked.</param>
        /// <param name="canExecute">The predicate that determines whether execution is currently allowed.</param>
        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        /// <summary>
        /// Occurs when changes occur that affect whether or not the command should execute.
        /// Hooks into WPF CommandManager.RequerySuggested for automatic UI button state updates.
        /// </summary>
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        /// <summary>
        /// Determines whether the command can execute in its current state.
        /// </summary>
        /// <param name="parameter">Data used by the command (optional).</param>
        /// <returns>True if the command can execute; otherwise, false.</returns>
        public bool CanExecute(object? parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        /// <summary>
        /// Executes the command payload.
        /// </summary>
        /// <param name="parameter">Data used by the command (optional).</param>
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                _execute(parameter);
            }
        }

        /// <summary>
        /// Manually triggers a re-evaluation of the CanExecute logic.
        /// </summary>
        public void RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
