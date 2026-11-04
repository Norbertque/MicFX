using MicFX.Models;
using Xunit;

namespace MicFX.Tests;

/// <summary>MicFX's own copies of soundboard clips.</summary>
public sealed class SoundLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"micfx-lib-{Guid.NewGuid():N}");
    private readonly SoundLibrary library;

    public SoundLibraryTests()
    {
        Directory.CreateDirectory(root);
        library = new SoundLibrary(Path.Combine(root, "Sounds"));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Original(string name, string content)
    {
        string dir = Path.Combine(root, "originals", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Import_CopiesIntoTheLibrary_AndLeavesTheOriginal()
    {
        string original = Original("bonk.mp3", "audio");
        string copy = library.Import(original);

        Assert.True(library.Contains(copy));
        Assert.Equal("bonk.mp3", Path.GetFileName(copy));
        Assert.Equal("audio", File.ReadAllText(copy));
        Assert.True(File.Exists(original));
        Assert.False(library.Contains(original));
    }

    [Fact]
    public void Import_ReusesAnIdenticalCopy()
    {
        string first = library.Import(Original("bonk.mp3", "audio"));
        string second = library.Import(Original("bonk.mp3", "audio"));
        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(library.Folder));
    }

    [Fact]
    public void Import_NumbersADifferentFileWithTheSameName()
    {
        string first = library.Import(Original("bonk.mp3", "audio one"));
        string second = library.Import(Original("bonk.mp3", "audio two"));
        Assert.NotEqual(first, second);
        Assert.Equal("bonk (2).mp3", Path.GetFileName(second));
        Assert.Equal("audio two", File.ReadAllText(second));
    }

    [Fact]
    public void Import_OfALibraryFile_ReturnsItAsIs()
    {
        string copy = library.Import(Original("bonk.mp3", "audio"));
        Assert.Equal(copy, library.Import(copy));
    }

    [Fact]
    public void Release_DeletesOnlyLibraryFiles()
    {
        string original = Original("bonk.mp3", "audio");
        string copy = library.Import(original);

        library.Release(original);
        Assert.True(File.Exists(original));

        library.Release(copy);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void Contains_IsNotFooledByASimilarFolderName()
    {
        string lookalike = Path.Combine(root, "Sounds-old", "bonk.mp3");
        Assert.False(library.Contains(lookalike));
    }
}
