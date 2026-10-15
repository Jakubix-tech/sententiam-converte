using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SententiamConverte.Services;

namespace SententiamConverte.Views;

public partial class MainView : UserControl, IPlatformServices
{
    public MainView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppServices.Platform = this;
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
        return launcher != null && await launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
