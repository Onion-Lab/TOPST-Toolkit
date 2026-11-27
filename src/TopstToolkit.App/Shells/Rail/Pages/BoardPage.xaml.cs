using System.Windows;
using System.Windows.Controls;
using TopstToolkit.App.ViewModels;

namespace TopstToolkit.App.Shells.Rail.Pages;

public partial class BoardPage : UserControl
{
    public BoardPage() => InitializeComponent();

    /// <summary>The board's password goes to the view model as it is typed; a PasswordBox cannot be bound.</summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is BoardViewModel vm)
            vm.Password = PasswordInput.Password;
    }
}
