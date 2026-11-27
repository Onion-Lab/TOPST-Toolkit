using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TopstToolkit.App.Controls;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Shells.Rail;

/// <summary>Screen layout A: header, step rail on the left, one step per page, log drawer and task bar at the bottom.</summary>
public partial class RailShell : UserControl
{
    private ShellViewModel? _shell;

    public RailShell()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ShellViewModel);
    }

    private void Attach(ShellViewModel? shell)
    {
        if (_shell is not null)
        {
            _shell.Log.CollectionChanged -= OnLogChanged;
            _shell.PropertyChanged -= OnShellChanged;
        }
        _shell = shell;
        if (_shell is null)
            return;
        _shell.Log.CollectionChanged += OnLogChanged;
        _shell.PropertyChanged += OnShellChanged;
        UpdateLogChrome();
    }

    /// <summary>The drawer follows the newest line, like a terminal.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateLogChrome();
        LogScroll.ScrollToEnd();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.IsLogOpen))
            return;
        UpdateLogChrome();
        Dispatcher.BeginInvoke(LogScroll.ScrollToEnd);
    }

    private void UpdateLogChrome()
    {
        LogEmpty.Visibility = _shell is { Log.Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
        LogChevron.Glyph = _shell is { IsLogOpen: true } ? IconGlyph.ChevronDown : IconGlyph.ChevronUp;
    }
}
