using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Shells.Rail.Pages;

public partial class ZooPage : UserControl
{
    private ZooViewModel? _vm;

    public ZooPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnChanged;
            _vm = DataContext as ZooViewModel;
            if (_vm is not null)
                _vm.PropertyChanged += OnChanged;
            ShowChosen();
        };
        Loaded += (_, _) => ShowChosen();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ZooViewModel.Selected) or nameof(ZooViewModel.Tab))
            ShowChosen();
    }

    /// <summary>The chosen model is somewhere in a long list: keep its row in view (the mockup's list does the same).</summary>
    private void ShowChosen() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (_vm?.Selected is { } row && list.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement item)
        {
            var top = item.TransformToAncestor(rows).Transform(new Point(0, 0)).Y + rows.VerticalOffset;
            rows.ScrollToVerticalOffset(Math.Max(0, top - (rows.ViewportHeight - item.ActualHeight) / 2));
        }
    });
}
