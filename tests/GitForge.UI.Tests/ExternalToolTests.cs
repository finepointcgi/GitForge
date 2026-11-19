using GitForge.UI.Components;
using GitForge.UI.Services;

namespace GitForge.UI.Tests;

public class ExternalToolTests
{
    [Fact]
    public void Build_ReplacesThePlaceholder_OrAppendsThePath()
    {
        var wt = ExternalTools.Build("wt.exe -d {path}", @"C:\src\my repo");
        Assert.Equal("wt.exe", wt!.FileName);
        Assert.Equal(["-d", @"C:\src\my repo"], wt.Arguments);

        var npp = ExternalTools.Build("\"C:\\Program Files\\Notepad++\\notepad++.exe\" -multiInst", @"C:\src\a&b.txt");
        Assert.Equal(@"C:\Program Files\Notepad++\notepad++.exe", npp!.FileName);
        Assert.Equal(["-multiInst", @"C:\src\a&b.txt"], npp.Arguments); // passed as one argument, never through a shell
        Assert.False(npp.UseShell);
    }

    [Theory]
    [InlineData("code.cmd {path}")]
    [InlineData("C:\\tools\\edit.BAT")]
    [InlineData("   ")]
    public void Build_RefusesCommandsThatWouldRunThroughCmd(string template) => Assert.Null(ExternalTools.Build(template, @"C:\x"));

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("deploy.PS1", true)]
    [InlineData("run.bat", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("readme.md", false)]
    [InlineData("image.png", false)]
    [InlineData("Makefile", false)]
    public void IsRunnable_FlagsProgramsAndScripts(string file, bool runnable) => Assert.Equal(runnable, ExternalTools.IsRunnable(file));

    [Theory]
    [InlineData("https://github.com/org/library.git", "library")]
    [InlineData("git@github.com:org/library.git", "library")]
    [InlineData("https://dev.azure.com/org/project/_git/Library/", "Library")]
    [InlineData(@"\\server\share\repos\tools", "tools")]
    public void SubmodulePath_SuggestsTheRepositoryName(string url, string expected) => Assert.Equal(expected, SubmodulePath.Suggest(url));

    [Theory]
    [InlineData("libs/library", null)]
    [InlineData(@"libs\library", null)]
    [InlineData("", "Required")]
    [InlineData("../elsewhere", "Use a folder inside this repository, such as libs/name.")]
    [InlineData("C:/abs", "Use a folder inside this repository, such as libs/name.")]
    [InlineData(".git/modules/x", "Choose a folder outside .git.")]
    public void SubmodulePath_ValidatesFolders(string path, string? error) => Assert.Equal(error, SubmodulePath.Validate(path, null));

    [Fact]
    public void WorktreePath_IsBesideTheRepository()
    {
        var repo = Path.Combine(Path.GetTempPath(), "app");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "app-feature-login"), WorktreePath.Suggest(repo, "feature/login"));
    }

    [Fact]
    public void IgnorePatterns_OfferFileExtensionAndFolder()
    {
        Assert.Equal(
            [("This file", "/build/out/app.log"), ("All .log files", "*.log"), ("Its folder", "/build/out/")],
            FileContextMenu.IgnorePatterns("build/out/app.log").ToList());
        Assert.Equal([("This file", "/Makefile.local")], FileContextMenu.IgnorePatterns("Makefile.local").Take(1).ToList());
    }
}
