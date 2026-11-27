using System.Windows;
using System.Windows.Controls;

namespace TopstToolkit.App.Shells.Rail.Pages;

public partial class ResultPage : UserControl
{
    /// <summary>Height kept free for the titles of the side-by-side view and the controls row under the picture.</summary>
    private const double Reserved = 72;

    public ResultPage() => InitializeComponent();

    /// <summary>The picture may grow until the controls row under it reaches the bottom of the page.</summary>
    private void OnCentreSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var max = Math.Max(160, CentreHost.ActualHeight - Reserved);
        PrimaryView.MaxHeight = max;
        SecondaryView.MaxHeight = max;
    }
}
