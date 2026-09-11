using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

/// <summary>
/// キャンバス未設定のプロジェクトを読み込んだときの初回サイズ選択（段階 4.4）。
/// モーダルだが再生・出力は別スレッドで継続する。
/// </summary>
internal partial class CanvasSelectDialog : Window
{
    private const int MinimumDimension = 16;
    private const int MaximumDimension = 16384;

    private readonly string _defaultFitId;
    private bool _isUpdatingPreset;

    public CanvasSettings? Selection { get; private set; }

    public CanvasSelectDialog(int width, int height, string defaultFitId)
    {
        _defaultFitId = defaultFitId;
        InitializeComponent();
        CanvasDialogWidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
        CanvasDialogHeightBox.Text = height.ToString(CultureInfo.InvariantCulture);
        SelectPresetFor(width, height);
    }

    private void SelectPresetFor(int width, int height)
    {
        _isUpdatingPreset = true;
        try
        {
            CanvasDialogPresetCombo.SelectedIndex = (width, height) switch
            {
                (1920, 1080) => 0,
                (3840, 2160) => 1,
                (1080, 1920) => 2,
                _ => 3,
            };
            ApplyPresetEditing();
        }
        finally
        {
            _isUpdatingPreset = false;
        }
    }

    private void ApplyPresetEditing()
    {
        bool custom = CanvasDialogPresetCombo.SelectedIndex == 3;
        CanvasDialogWidthBox.IsEnabled = custom;
        CanvasDialogHeightBox.IsEnabled = custom;
    }

    private void CanvasDialogPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPreset) return;
        ApplyPresetEditing();
        if (CanvasDialogPresetCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string preset || preset == "custom") return;
        string[] parts = preset.Split('x');
        CanvasDialogWidthBox.Text = parts[0];
        CanvasDialogHeightBox.Text = parts[1];
    }

    private void CanvasDialogOk_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadDimension(CanvasDialogWidthBox.Text, out int width) ||
            !TryReadDimension(CanvasDialogHeightBox.Text, out int height))
        {
            MessageBox.Show(this, "キャンバス寸法は 16〜16384 の整数で入力してください。",
                "キャンバスサイズ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Selection = new CanvasSettings(width, height, _defaultFitId);
        DialogResult = true;
    }

    private static bool TryReadDimension(string text, out int value)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
           && value is >= MinimumDimension and <= MaximumDimension;
}
