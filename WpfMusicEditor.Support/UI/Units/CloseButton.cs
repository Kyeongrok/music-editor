using System.Windows;
using System.Windows.Controls;

namespace WpfMusicEditor.Support.UI.Units;

public class CloseButton : Button
{
    static CloseButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CloseButton),
            new FrameworkPropertyMetadata(typeof(CloseButton)));
    }
}
