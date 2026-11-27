using System.Windows;
using System.Windows.Controls;
using TopstToolkit.App.Controls;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Shells.Rail.Pages;

public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e) => DropFiles.Accept(e);

    /// <summary>A model file dropped here becomes a new project.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is HomeViewModel vm && DropFiles.First(e) is { } path)
            vm.ImportDropped(path);
    }
}
