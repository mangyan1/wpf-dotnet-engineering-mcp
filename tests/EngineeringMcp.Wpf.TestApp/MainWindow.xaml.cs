using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using FluentWindow = global::Wpf.Ui.Controls.FluentWindow;

namespace EngineeringMcpFixture;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new FixtureViewModel();
    }

    private void CrashButton_Click(object sender, RoutedEventArgs e)
        => throw new InvalidOperationException("Synthetic fixture exception. fake@example.com token=fixture-secret-value");

    private void FocusMountPathButton_Click(object sender, RoutedEventArgs e)
        => MountPathBox.Focus();
}

public sealed class FixtureViewModel : INotifyPropertyChanged
{
    private string _mountPath = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string MountPath
    {
        get => _mountPath;
        set
        {
            _mountPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MountPath)));
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public FixtureCommand SaveCommand { get; }

    public FixtureViewModel()
    {
        SaveCommand = new FixtureCommand(() => { }, () => !string.IsNullOrWhiteSpace(MountPath));
    }
}

public sealed class FixtureCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
