using System.Windows;
using System.Windows.Controls;
using TopstToolkit.App.Controls;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Shells.Rail.Pages;

public partial class CheckPage : UserControl
{
    public CheckPage() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e) => DropFiles.Accept(e);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is ModelCheckViewModel vm && DropFiles.First(e) is { } path)
            await vm.CheckAsync(path);
    }
}
