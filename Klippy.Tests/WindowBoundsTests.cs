using System;
using System.IO;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// window.json: where the launcher window was left and how big, kept between runs. The arithmetic of putting
/// it back is <see cref="PlacementPolicyTests"/>'; this is the file.
/// </summary>
[Collection("storage-locations")] // mutates StorageLocations statics
public class WindowBoundsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"klippy-window-{Guid.NewGuid():N}");
    private readonly string _originalDir = StorageLocations.Directory;
    private readonly string _originalDefaultDir = StorageLocations.DefaultDirectory;

    private string FilePath => Path.Combine(_root, "window.json");

    public void Dispose()
    {
        StorageLocations.Directory = _originalDir;
        StorageLocations.DefaultDirectory = _originalDefaultDir;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void WhatIsSaved_IsWhatIsLoaded()
    {
        var bounds = new WindowBounds { X = -1700, Y = 120, Width = 812.5, Height = 640 };
        bounds.Save(FilePath);

        Assert.Equal(bounds, WindowBounds.Load(FilePath));
    }

    [Fact]
    public void NothingSavedYet_IsNothingToRestore() =>
        Assert.Null(WindowBounds.Load(FilePath));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "X": 10, "Y": 10, "Width": 0, "Height": 560 }""")]
    [InlineData("""{ "X": 10, "Y": 10, "Width": 680, "Height": -1 }""")]
    [InlineData("""{ "X": 10, "Y": 10 }""")]
    public void AFileThatDescribesNoWindow_IsIgnored_RatherThanThrown(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(FilePath, json);

        Assert.Null(WindowBounds.Load(FilePath));
    }

    [Fact]
    public void ItStaysWithThisMachine_WhereverTheDataFolderGoes()
    {
        // A position is a place on this machine's monitors. The data folder is a thing people point at a synced
        // drive, and a laptop opening its window where a desktop's second monitor was would be a lost window.
        StorageLocations.DefaultDirectory = Path.Combine(_root, "appdata");
        StorageLocations.Directory = Path.Combine(_root, "synced");

        Assert.Equal(Path.Combine(_root, "appdata", "window.json"), StorageLocations.WindowPath);
    }
}
