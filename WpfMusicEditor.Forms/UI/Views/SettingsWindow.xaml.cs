using System.Windows;
using WpfMusicEditor.Forms.ViewModels;

namespace WpfMusicEditor.Forms.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
