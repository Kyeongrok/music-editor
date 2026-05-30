using System.Windows;
using WpfMusicEditor.Forms.ViewModels;

namespace WpfMusicEditor.Forms.UI.Views;

public partial class TrainingWindow : Window
{
    public TrainingWindow(TrainingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
