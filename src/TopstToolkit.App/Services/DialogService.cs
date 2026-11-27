using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TopstToolkit.App.Services;

/// <summary>
/// Windows dialogs and shell actions behind one seam, so view models stay free of windows (and screen captures and
/// tests can replace it). Shared by every screen layout (D9).
/// </summary>
public class DialogService
{
    public const string ModelFilter = "모델 파일 (*.onnx;*.tflite;*.cfg;*.enlight)|*.onnx;*.tflite;*.cfg;*.enlight|모든 파일 (*.*)|*.*";
    public const string ImageFilter = "사진 (*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|모든 파일 (*.*)|*.*";

    public virtual string? PickModelFile() => PickFile("모델 파일 고르기", ModelFilter);

    public virtual string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public virtual IReadOnlyList<string> PickImages()
    {
        var dialog = new OpenFileDialog { Title = "사진 고르기", Filter = ImageFilter, CheckFileExists = true, Multiselect = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public virtual string? PickFolder(string title, string? initial = null)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
            dialog.InitialDirectory = initial;
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    /// <summary>A yes/no question before something that cannot be undone.</summary>
    public virtual bool Confirm(string title, string message) =>
        (Owner is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            : MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)) == MessageBoxResult.OK;

    /// <summary>Opens a folder in File Explorer, or selects a file in its folder.</summary>
    public virtual void ShowInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // File Explorer not starting is not worth an error dialog
        }
    }

    /// <summary>Opens a file with the program Windows associates with it (README in the default editor, a picture in the viewer).</summary>
    public virtual void OpenWithShell(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>Opens an https address in the default browser (a license page of a component).</summary>
    public virtual void OpenUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;
}
