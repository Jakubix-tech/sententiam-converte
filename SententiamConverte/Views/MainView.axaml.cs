using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SententiamConverte.Services;

namespace SententiamConverte.Views;

public partial class MainView : UserControl, IPlatformServices
{
    private TopLevel? _topLevel;

    public MainView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppServices.Platform = this;
        // F1 öffnet die Hilfe – am Fenster angemeldet, damit es auch ohne Fokus wirkt
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnF1, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnF1);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnF1(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1 || DataContext is not ViewModels.MainViewModel vm) return;
        vm.SelectedTab = ViewModels.MainViewModel.HelpTab;
        e.Handled = true;
    }

    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;

    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync(string title, IReadOnlyList<string> patterns, bool multiple)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter =
            [
                new FilePickerFileType("Unterstützte Dateien") { Patterns = patterns.ToList() },
                new FilePickerFileType("Alle Dateien") { Patterns = ["*"] },
            ],
        });
        return files.Select(f => new PickedFile(f.Name, () => f.OpenReadAsync())).ToList();
    }

    public async Task<Stream?> SaveFileAsync(string title, string suggestedName)
    {
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.'),
        });
        return file == null ? null : await file.OpenWriteAsync();
    }

    public async Task<bool> OpenFileAsync(string path)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher == null) return false;
        return Directory.Exists(path)
            ? await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path))
            : await launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
