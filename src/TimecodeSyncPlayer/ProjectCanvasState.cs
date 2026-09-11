using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

/// <summary>
/// プロジェクトのキャンバス設定を UI スレッドで保持する（段階 4.2）。
/// 新規は 1920x1080・fit-height。Canvas=null のプロジェクトを読み込んだ場合は
/// 未設定のまま 1920x1080 を仮採用し、保存時に確定する。
/// </summary>
internal sealed class ProjectCanvasState
{
    public CanvasSettings Current { get; private set; } = CanvasSettings.Default;

    /// <summary>Canvas=null のプロジェクトを読み込んだ（次回保存で書き込む）。</summary>
    public bool IsUnsetInProject { get; private set; }

    /// <summary>読み込んだプロジェクトの値から変更されている。</summary>
    public bool IsDirty { get; private set; }

    public void OnProjectLoaded(CanvasData? canvas)
    {
        if (canvas is null)
        {
            Current = CanvasSettings.Default;
            IsUnsetInProject = true;
        }
        else
        {
            Current = new CanvasSettings(
                canvas.Width,
                canvas.Height,
                canvas.DefaultFit ?? FitHeight.FitId);
            IsUnsetInProject = false;
        }
        IsDirty = false;
    }

    /// <summary>サイズ選択ダイアログの結果または適用ボタンによる変更。</summary>
    public void Select(CanvasSettings settings)
    {
        Current = settings;
        IsDirty = true;
    }

    /// <summary>保存が成功した。以後 IsUnsetInProject は false。</summary>
    public void MarkSaved()
    {
        IsUnsetInProject = false;
        IsDirty = false;
    }

    /// <summary>保存形式へ書き出す正規化済みの値。</summary>
    public CanvasData ToData() => new()
    {
        Width = Current.Width,
        Height = Current.Height,
        DefaultFit = Current.DefaultFitId,
    };
}
