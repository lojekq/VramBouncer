using VramBouncer.Features;

namespace VramBouncer.Tests;

[TestClass]
public class GamesTests
{
    [TestMethod]
    public void SteamLibrariesWithEscapedSlashes()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"228980"		"635553174"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            		"apps"
            		{
            			"570"		"78077750873"
            		}
            	}
            }
            """;
        CollectionAssert.AreEqual(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, SteamLibraryParser.Libraries(vdf));
    }

    [TestMethod]
    public void SteamManifestGivesNameAndInstallDir()
    {
        const string acf = """
            "AppState"
            {
            	"appid"		"570"
            	"name"		"Dota 2"
            	"StateFlags"		"4"
            	"installdir"		"dota 2 beta"
            	"UserConfig"
            	{
            		"language"		"english"
            	}
            }
            """;
        Assert.AreEqual(("570", "Dota 2", "dota 2 beta"), SteamLibraryParser.Manifest(acf));
        Assert.IsNull(SteamLibraryParser.Manifest("\"AppState\" { \"appid\" \"1\" \"name\" \"x\" }"));
    }

    [TestMethod]
    public void EpicSkipsIncompleteAndEmptyLocation()
    {
        var g = EpicManifestParser.Parse("""{"DisplayName":"Fortnite","InstallLocation":"D:\\Epic Games/Fortnite","LaunchExecutable":"FortniteGame/Binaries/Win64/FortniteLauncher.exe","bIsIncompleteInstall":false}""");
        Assert.AreEqual(new Game("Fortnite", "Epic", @"D:\Epic Games/Fortnite", "FortniteLauncher.exe"), g);
        Assert.IsNull(EpicManifestParser.Parse("""{"DisplayName":"X","InstallLocation":"D:\\X","bIsIncompleteInstall":true}"""));
        Assert.IsNull(EpicManifestParser.Parse("""{"DisplayName":"X","InstallLocation":""}"""));
    }

    static readonly GameMatcher M = new(
        [new("Dota 2", "Steam", @"D:\SteamLibrary\steamapps\common\dota 2 beta"),
         new("Emu", "Manual", @"C:\Emu\emu.exe", "emu.exe"),
         new("Tools", "Manual", @"C:\Games")],
        [@"D:\SteamLibrary\steamapps\common\wallpaper_engine", @"C:\Games\Tools"],
        GameMatcher.DefaultPatterns);

    [TestMethod]
    public void MatcherHonoursCaseAndFolderBoundary()
    {
        Assert.AreEqual("Dota 2", M.Match(@"D:\SteamLibrary\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe", "dota2")?.Name);
        Assert.AreEqual("Dota 2", M.Match(@"d:\steamlibrary\STEAMAPPS\common\Dota 2 Beta\game\bin\win64\dota2.exe", "dota2")?.Name);
        Assert.AreEqual("Dota 2", M.Match(@"\\?\D:\SteamLibrary\steamapps\common\dota 2 beta\dota2.exe", "dota2")?.Name);
        Assert.IsNull(M.Match(@"D:\SteamLibrary\steamapps\common\dota 2 beta2\dota2.exe", "dota2"));
        Assert.AreEqual("Emu", M.Match(@"C:\Emu\emu.exe", "emu")?.Name);
        Assert.IsNull(M.Match(@"C:\Emu\other.exe", "other"));
    }

    [TestMethod]
    public void ExclusionsByFolderAndPatternWin()
    {
        Assert.IsNull(M.Match(@"D:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper64.exe", "wallpaper64"));
        Assert.IsNull(M.Match(@"C:\Games\Tools\x.exe", "x"));
        Assert.AreEqual("Tools", M.Match(@"C:\Games\Real\x.exe", "x")?.Name);
        Assert.IsNull(M.Match(@"D:\SteamLibrary\steamapps\common\dota 2 beta\UnityCrashHandler64.exe", "UnityCrashHandler64"));
        Assert.IsNull(M.Match(@"D:\SteamLibrary\steamapps\common\dota 2 beta\_CommonRedist\vc_redist.x64.exe", "vc_redist.x64"));
    }

    [TestMethod]
    public void UnreadablePathMatchesByExeName()
    {
        Assert.AreEqual("Emu", M.Match(null, "emu")?.Name);
        Assert.IsNull(M.Match(null, "dota2")); // Steam games have no known exe name
    }
}
