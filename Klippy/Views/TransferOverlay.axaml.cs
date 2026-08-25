using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Klippy.ViewModels;

namespace Klippy.Views;

public partial class TransferOverlay : UserControl
{
    private static readonly FilePickerFileType JsonFileType = new("JSON files")
    {
        Patterns = new[] { "*.json" },
        AppleUniformTypeIdentifiers = new[] { "public.json" },
        MimeTypes = new[] { "application/json" },
    };

    private TransferViewModel? Vm => (DataContext as MainViewModel)?.Transfer;

    public TransferOverlay()
    {
        InitializeComponent();
    }

    private void ScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel { Transfer: { } transfer })
            transfer.CloseCommand.Execute(null);
    }

    private async void ExportClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            if (top.StorageProvider.CanSave)
            {
                var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export snippets",
                    SuggestedFileName = vm.SuggestedFileName,
                    DefaultExtension = "json",
                    FileTypeChoices = new[] { JsonFileType },
                });
                if (file is null) return;
                await using var stream = await file.OpenWriteAsync();
                vm.ExportTo(stream);
            }
            else
            {
                // No save dialog on this platform (e.g. iOS): write to the app's documents folder.
                var dir = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create);
                var path = Path.Combine(dir, vm.SuggestedFileName);
                await using var stream = File.Create(path);
                vm.ExportTo(stream);
                vm.StatusText += $" to {path}";
            }
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Export failed: {ex.Message}";
        }
    }

    private async void PickImportClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import snippets",
                AllowMultiple = false,
                FileTypeFilter = new[] { JsonFileType, FilePickerFileTypes.All },
            });
            if (files.FirstOrDefault() is not { } file) return;
            await using var stream = await file.OpenReadAsync();
            vm.LoadImportPreview(stream, file.Name);
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Import failed: {ex.Message}";
        }
    }
}
