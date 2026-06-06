using System.Windows;
using WpfMusicEditor.Forms.ViewModels;

namespace WpfMusicEditor.Forms.UI.Views;

public partial class InsertAudioWindow : Window
{
    private readonly InsertAudioViewModel _viewModel;

    public InsertAudioWindow(InsertAudioViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // 미리듣기 플레이어/타이머를 창이 닫힐 때 정리한다.
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>확정(삽입) 시 호출 측이 선택 구간을 가져오는 진입점.</summary>
    public InsertAudioViewModel ViewModel => _viewModel;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanConfirm)
            return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
