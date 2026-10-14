using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenATLAS.Workbench.ViewModels
{
    /// <summary>
    /// Base class for all ViewModels implementing the INotifyPropertyChanged contract.
    /// Provides thread-safe, allocation-efficient property change notification for WPF data binding.
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        /// <summary>
        /// Occurs when a property value changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raises the PropertyChanged event for the specified property name.
        /// Uses CallerMemberName to eliminate magic string property names.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Compares the current field value against a new value, updates the backing field if changed,
        /// and fires the PropertyChanged event. Returns true if the value was updated.
        /// </summary>
        /// <typeparam name="T">The type of the property.</typeparam>
        /// <param name="field">Reference to the backing field.</param>
        /// <param name="value">The new value to assign.</param>
        /// <param name="propertyName">The name of the property (auto-populated by compiler).</param>
        /// <returns>True if the field was mutated; false if the value was identical.</returns>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
