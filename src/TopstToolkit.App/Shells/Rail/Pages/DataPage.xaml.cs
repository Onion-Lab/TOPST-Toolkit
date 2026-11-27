using System.Windows;
using System.Windows.Controls;
using TopstToolkit.App.Controls;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Shells.Rail.Pages;

public partial class DataPage : UserControl
{
    public DataPage() => InitializeComponent();

    private void OnTestDragOver(object sender, DragEventArgs e) => DropFiles.Accept(e);

    /// <summary>Pictures dropped on the right column become check pictures.</summary>
    private async void OnTestDrop(object sender, DragEventArgs e)
    {
        if (DataContext is DataViewModel vm)
            await vm.AddTestFilesAsync(DropFiles.All(e));
    }
}
