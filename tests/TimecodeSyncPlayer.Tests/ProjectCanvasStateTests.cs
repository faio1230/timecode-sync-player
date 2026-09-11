using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>段階 4.2: ProjectCanvasState の初期値・読込・キャンセル仮採用・保存確定。</summary>
public class ProjectCanvasStateTests
{
    [Fact]
    public void NewProject_UsesDefaultCanvasAndIsNotUnset()
    {
        var state = new ProjectCanvasState();

        state.Current.Should().Be(CanvasSettings.Default);
        state.Current.Width.Should().Be(1920);
        state.Current.Height.Should().Be(1080);
        state.Current.DefaultFitId.Should().Be(FitHeight.FitId);
        state.IsUnsetInProject.Should().BeFalse();
        state.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void OnProjectLoaded_WithCanvas_UsesProjectValue()
    {
        var state = new ProjectCanvasState();

        state.OnProjectLoaded(new CanvasData { Width = 3840, Height = 2160, DefaultFit = FitWidth.FitId });

        state.Current.Width.Should().Be(3840);
        state.Current.Height.Should().Be(2160);
        state.Current.DefaultFitId.Should().Be(FitWidth.FitId);
        state.IsUnsetInProject.Should().BeFalse();
        state.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void OnProjectLoaded_WithoutCanvas_AdoptsDefaultAndStaysUnset()
    {
        var state = new ProjectCanvasState();

        state.OnProjectLoaded(null);

        state.Current.Should().Be(CanvasSettings.Default);
        state.IsUnsetInProject.Should().BeTrue();
        state.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void CancelFirstSelection_KeepsProvisionalDefaultAndWritesItOnSave()
    {
        var state = new ProjectCanvasState();
        state.OnProjectLoaded(null);

        // ダイアログをキャンセルした場合: 値を変更せず仮採用の 1920x1080 のまま保存される。
        CanvasData saved = state.ToData();

        saved.Width.Should().Be(1920);
        saved.Height.Should().Be(1080);
        saved.DefaultFit.Should().Be(FitHeight.FitId);

        state.MarkSaved();

        state.IsUnsetInProject.Should().BeFalse();
        state.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void SelectForUnsetProject_MarksDirtyAndPreservesUnsetUntilSaved()
    {
        var state = new ProjectCanvasState();
        state.OnProjectLoaded(null);

        state.Select(new CanvasSettings(3840, 2160, FitWidth.FitId));

        state.Current.Width.Should().Be(3840);
        state.IsUnsetInProject.Should().BeTrue("選択結果は次回保存で書き込む");
        state.IsDirty.Should().BeTrue();

        state.MarkSaved();

        state.IsUnsetInProject.Should().BeFalse();
        state.IsDirty.Should().BeFalse();
        state.ToData().Width.Should().Be(3840);
    }
}
