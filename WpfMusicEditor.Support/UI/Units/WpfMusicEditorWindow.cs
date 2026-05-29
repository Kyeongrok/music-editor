using System.Windows;

namespace WpfMusicEditor.Support.UI.Units;

public class WpfMusicEditorWindow : Window
{
    static WpfMusicEditorWindow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(WpfMusicEditorWindow),
            new FrameworkPropertyMetadata(typeof(WpfMusicEditorWindow)));
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Maximized)
            MaxHeight = SystemParameters.WorkArea.Height;
        else
            MaxHeight = double.PositiveInfinity;
    }
}
