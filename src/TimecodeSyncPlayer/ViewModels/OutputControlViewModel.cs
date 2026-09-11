using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TimecodeSyncPlayer.ViewModels;

/// <summary>
/// 出力グループの UI 状態（テストカードのトグル）。トグルはカード表示だけを変え、
/// タイムライン状態・再生状態へ影響しない（段階 4.4/4.5）。
/// </summary>
internal sealed class OutputControlViewModel : INotifyPropertyChanged
{
    private bool _testCardEnabled;

    public bool TestCardEnabled => _testCardEnabled;

    public string TestCardToggleLabel =>
        ToggleLabelFormatter.Format(_testCardEnabled, "Card: ON", "Card: OFF");

    /// <summary>起動時の初期値（環境変数）。プロジェクト切替では変更しない。</summary>
    public void InitializeTestCard(bool enabled)
    {
        _testCardEnabled = enabled;
        OnPropertyChanged(nameof(TestCardEnabled));
        OnPropertyChanged(nameof(TestCardToggleLabel));
    }

    public void ToggleTestCard()
    {
        _testCardEnabled = !_testCardEnabled;
        OnPropertyChanged(nameof(TestCardEnabled));
        OnPropertyChanged(nameof(TestCardToggleLabel));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
