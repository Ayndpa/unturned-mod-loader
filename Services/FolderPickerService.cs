using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace UnturnedModLoader.Services;

public class FolderPickerService(Window owner)
{
    public async Task<string?> PickFolderAsync(string title, string? suggestedStartLocation = null)
    {
        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        };

        if (!string.IsNullOrWhiteSpace(suggestedStartLocation) && Directory.Exists(suggestedStartLocation))
        {
            try
            {
                var folder = await owner.StorageProvider.TryGetFolderFromPathAsync(suggestedStartLocation);
                if (folder is not null)
                    options.SuggestedStartLocation = folder;
            }
            catch
            {
                // Ignore invalid suggested paths.
            }
        }

        var result = await owner.StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }
}