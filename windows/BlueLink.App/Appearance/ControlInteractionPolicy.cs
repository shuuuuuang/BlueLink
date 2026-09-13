using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace BlueLink.Appearance;

// Preserve the official templates, input focus feedback, selection and keyboard navigation.
internal static class ControlInteractionPolicy
{
    public static void Initialize()
    {
        // Some official controls (including caption buttons) use dedicated focus-adornment styles.
        Control.FocusVisualStyleProperty.OverrideMetadata(typeof(Control),
            new FrameworkPropertyMetadata(null, null, static (_, _) => null));
        FrameworkElement.CursorProperty.OverrideMetadata(typeof(ButtonBase), new FrameworkPropertyMetadata(Cursors.Hand));
        FrameworkElement.CursorProperty.OverrideMetadata(typeof(MenuItem), new FrameworkPropertyMetadata(Cursors.Hand));
        foreach (var type in new[] { typeof(ButtonBase), typeof(MenuItem), typeof(Hyperlink) })
            EventManager.RegisterClassHandler(type, Mouse.QueryCursorEvent, new QueryCursorEventHandler(Button_QueryCursor), true);
    }

    private static void Button_QueryCursor(object sender, QueryCursorEventArgs e)
    {
        if (sender is UIElement { IsEnabled: false } or ContentElement { IsEnabled: false }) return;
        // Includes buttons inside official templates that explicitly specify an arrow cursor.
        e.Cursor = Cursors.Hand;
        e.Handled = true;
    }
}
