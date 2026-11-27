using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Shells.Rail.Pages;

/// <summary>
/// "자를 곳" in layout A. Only what belongs to this view lives here: moving the drawing when the view model asks for
/// an operator to be in sight, and keeping the strip beside the drawing in step with the scroll position.
/// </summary>
public partial class GraphPage : UserControl
{
    /// <summary>A drawing a little wider than the page is shown a little smaller instead of scrolling sideways.</summary>
    private const double SmallestScale = 0.8;

    private GraphCutViewModel? _viewModel;
    private double _scale = 1;

    public GraphPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as GraphCutViewModel);
        Scroll.ScrollChanged += (_, _) =>
        {
            Fit();
            UpdateMap();
        };
        Scroll.SizeChanged += (_, _) => Fit();
        Map.Seek += position => Scroll.ScrollToVerticalOffset(position * Scroll.ExtentHeight - Scroll.ViewportHeight / 2);
    }

    private void Attach(GraphCutViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollRequested -= ShowOperator;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.ScrollRequested += ShowOperator;
            viewModel.PropertyChanged += OnViewModelChanged;
        }
        Fit();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphCutViewModel.Layout))
            Fit();
    }

    private void Fit()
    {
        if (_viewModel?.Layout is not { Width: > 0 } layout || Scroll.ViewportWidth <= 0)
            return;
        var scale = Math.Clamp((Scroll.ViewportWidth - 2) / layout.Width, SmallestScale, 1.0);
        if (Math.Abs(scale - _scale) < 0.004 && Graph.LayoutTransform is ScaleTransform)
            return;
        _scale = scale;
        Graph.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private void ShowOperator(int id)
    {
        if (_viewModel?.Layout?.BoxOf(id) is not { } box)
            return;
        Scroll.ScrollToVerticalOffset(Math.Max(0, (box.Y + box.Height / 2) * _scale - Scroll.ViewportHeight / 2));
        if (Scroll.ExtentWidth > Scroll.ViewportWidth)
            Scroll.ScrollToHorizontalOffset(Math.Max(0, box.CenterX * _scale - Scroll.ViewportWidth / 2));
    }

    private void UpdateMap()
    {
        var extent = Scroll.ExtentHeight;
        Map.ViewTop = extent > 0 ? Scroll.VerticalOffset / extent : 0;
        Map.ViewHeight = extent > 0 ? Math.Min(1, Scroll.ViewportHeight / extent) : 1;
    }
}
