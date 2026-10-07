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

    private string _spoutStatusText = "";

    /// <summary>v0.6.6 R-11: Spout が ON のときの「Spout: 送信名」。OFF は空（表示しない）。</summary>
    public string SpoutStatusText => _spoutStatusText;

    public void SetSpoutStatus(bool enabled, string senderName)
    {
        string text = SpoutStatusFormatter.Format(enabled, senderName);
        if (text == _spoutStatusText) return;
        _spoutStatusText = text;
        OnPropertyChanged(nameof(SpoutStatusText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
