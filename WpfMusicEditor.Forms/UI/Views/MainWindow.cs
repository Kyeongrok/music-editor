using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfMusicEditor.Forms.ViewModels;
using WpfMusicEditor.Support.UI.Units;

namespace WpfMusicEditor.Forms.UI.Views;

public class MainWindow : WpfMusicEditorWindow
{
    static MainWindow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(MainWindow),
            new FrameworkPropertyMetadata(typeof(MainWindow)));
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;

        // Ctrl+Z → 실행 취소
        InputBindings.Add(new KeyBinding(viewModel.UndoCommand, Key.Z, ModifierKeys.Control));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var minimizeButton = GetTemplateChild("PART_MinimizeButton") as Button;
        if (minimizeButton != null)
            minimizeButton.Click += (s, e) => WindowState = System.Windows.WindowState.Minimized;

        var maximizeButton = GetTemplateChild("PART_MaximizeButton") as Button;
        if (maximizeButton != null)
            maximizeButton.Click += (s, e) =>
                WindowState = WindowState == System.Windows.WindowState.Maximized
                    ? System.Windows.WindowState.Normal
                    : System.Windows.WindowState.Maximized;

        var closeButton = GetTemplateChild("PART_CloseButton") as Button;
        if (closeButton != null)
            closeButton.Click += (s, e) => Close();
    }
}
