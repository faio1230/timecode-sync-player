using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class AppLaunchArgumentsTests
{
    [Fact]
    public void Parse_ReadsOpenAndPlaylistPaths()
    {
        AppLaunchArguments result = AppLaunchArguments.Parse([
            "TimecodeSyncPlayer.exe",
            "--open", @"C:\media\one.mp4",
            "--playlist", @"C:\media\two.mp4", @"C:\media\three.mp4"
        ]);

        result.OpenPath.Should().Be(@"C:\media\one.mp4");
        result.PlaylistPaths.Should().Equal(@"C:\media\two.mp4", @"C:\media\three.mp4");
    }

    [Fact]
    public void Parse_ReadsLoadAndSaveProjectWithSeparateValues()
    {
        AppLaunchArguments result = AppLaunchArguments.Parse([
            "TimecodeSyncPlayer.exe",
            "--load-project", @"C:\projects\show.tsp",
            "--save-project", @"C:\projects\out.tsp"
        ]);

        result.LoadProjectPath.Should().Be(@"C:\projects\show.tsp");
        result.SaveProjectPath.Should().Be(@"C:\projects\out.tsp");
    }

    [Fact]
    public void Parse_ReadsLoadAndSaveProjectWithEqualsValues()
    {
        AppLaunchArguments result = AppLaunchArguments.Parse([
            "TimecodeSyncPlayer.exe",
            @"--load-project=C:\projects\show.tsp",
            @"--save-project=C:\projects\out.tsp"
        ]);

        result.LoadProjectPath.Should().Be(@"C:\projects\show.tsp");
        result.SaveProjectPath.Should().Be(@"C:\projects\out.tsp");
    }

    [Fact]
    public void InitialPlaylistPaths_PrependsOpenPathWhenPlaylistIsSpecified()
    {
        AppLaunchArguments result = AppLaunchArguments.Parse([
            "TimecodeSyncPlayer.exe",
            "--open", @"C:\media\one.mp4",
            "--playlist", @"C:\media\two.mp4"
        ]);

        result.InitialPlaylistPaths.Should().Equal(@"C:\media\one.mp4", @"C:\media\two.mp4");
    }
}
