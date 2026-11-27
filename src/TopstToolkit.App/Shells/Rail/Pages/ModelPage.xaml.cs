using System.Windows;
using System.Windows.Controls;
using TopstToolkit.App.Controls;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Shells.Rail.Pages;

public partial class ModelPage : UserControl
{
    public ModelPage() => InitializeComponent();

    private void OnDragOver(object sender, DragEventArgs e) => DropFiles.Accept(e);

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is ModelStepViewModel vm && DropFiles.First(e) is { } path)
            await vm.ImportAsync(path);
    }
}
