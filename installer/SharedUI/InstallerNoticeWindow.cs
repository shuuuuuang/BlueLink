using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using BlueLink.Presentation;

namespace BlueLink.Installation
{
    // Installation notices and confirmations share one composition and action layout.
    internal class InstallerDialogWindow : FluentWindow
    {
        internal bool Confirmed { get; private set; }
        internal string PrimaryText { get; }
        internal string Message { get; }

        internal InstallerDialogWindow(string title, string message, Window owner = null, string primaryText = "确定", string cancelText = null, bool destructive = false)
        {
            Title = title; PrimaryText = primaryText; Message = message;
            Width = WindowsDialogLayout.Width; MinHeight = WindowsDialogLayout.NoticeMinHeight;
            MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 40);
            SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            if (owner != null && owner.IsVisible) Owner = owner;
            ShowInTaskbar = false; ExtendsContentIntoTitleBar = true; WindowBackdropType = WindowBackdropType.None;
            FontFamily = owner?.TryFindResource("InstallerFont") as FontFamily ?? owner?.FontFamily ?? new FontFamily(new Uri("pack://application:,,,/" + typeof(InstallerDialogWindow).Assembly.GetName().Name + ";component/"), "./Assets/Fonts/#Noto Sans SC");
            SetResourceReference(StyleProperty, typeof(FluentWindow));
            Background = Brushes.White;
            var border = new Border { Background = Brushes.White, BorderBrush = ColorBrush("#DDE4EF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = WindowsDialogLayout.HeaderRow });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = WindowsDialogLayout.FooterRow });
            border.Child = layout; Content = border;
            var header = new Grid { Background = Brushes.Transparent };
            header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            header.Children.Add(new System.Windows.Controls.TextBlock { Name = "NoticeTitle", Text = title, FontFamily = FontFamily, ToolTip = title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 16, FontWeight = FontWeights.Medium, Foreground = ColorBrush("#172338"), Margin = new Thickness(16, 0, 62, 0), VerticalAlignment = VerticalAlignment.Center });
            var close = new Wpf.Ui.Controls.Button { Name = "NoticeClose", Width = 32, Height = 32, MinWidth = 0, MinHeight = 0, Padding = new Thickness(6), CornerRadius = new CornerRadius(8), Appearance = ControlAppearance.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, FocusVisualStyle = null, Content = new SymbolIcon { Symbol = SymbolRegular.Dismiss20, FontSize = 16, Foreground = ColorBrush("#64748B") } };
            System.Windows.Automation.AutomationProperties.SetName(close, "关闭");
            close.Click += (_, __) => Close(); header.Children.Add(close);
            header.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = ColorBrush("#DDE4EF") });
            layout.Children.Add(header);
            var body = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = WindowsDialogLayout.BodyMargin, MinHeight = 44 };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(new Border { Name = "NoticeIconTile", Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center, Background = ColorBrush(cancelText == null || destructive ? "#FFF1F0" : "#FFF8EA"), CornerRadius = new CornerRadius(10), Child = new SymbolIcon { Symbol = cancelText == null ? SymbolRegular.DismissCircle20 : SymbolRegular.Warning20, FontSize = 20, Foreground = ColorBrush(cancelText == null || destructive ? "#D92D20" : "#8A5700") } });
            var text = new System.Windows.Controls.TextBlock { Name = "NoticeText", Text = message, FontFamily = FontFamily, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = ColorBrush("#253249"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1); body.Children.Add(text);
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
            var primary = new Wpf.Ui.Controls.Button { Content = primaryText, FontFamily = FontFamily, MinWidth = 84, Height = WindowsDialogLayout.ActionHeight, MinHeight = WindowsDialogLayout.ActionHeight, Padding = new Thickness(10, 0, 10, 0), FontSize = 13, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Appearance = destructive ? ControlAppearance.Danger : ControlAppearance.Primary, Foreground = Brushes.White, Background = ColorBrush(destructive ? "#DB2F3D" : "#176BFF"), BorderBrush = ColorBrush(destructive ? "#DB2F3D" : "#176BFF"), MouseOverBackground = ColorBrush(destructive ? "#DB2F3D" : "#176BFF"), PressedBackground = ColorBrush(destructive ? "#DB2F3D" : "#176BFF"), PressedForeground = Brushes.White, IsDefault = cancelText == null, Cursor = Cursors.Hand, FocusVisualStyle = null };
            System.Windows.Automation.AutomationProperties.SetAutomationId(primary, "NoticePrimaryButton");
            primary.Click += (_, __) => { Confirmed = true; Close(); };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (!String.IsNullOrWhiteSpace(cancelText))
            {
                var cancel = new Wpf.Ui.Controls.Button { Content = cancelText, FontFamily = FontFamily, MinWidth = 76, Height = WindowsDialogLayout.ActionHeight, MinHeight = WindowsDialogLayout.ActionHeight, Padding = new Thickness(10, 0, 10, 0), FontSize = 13, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, Appearance = ControlAppearance.Secondary, IsDefault = true, FocusVisualStyle = null };
                System.Windows.Automation.AutomationProperties.SetAutomationId(cancel, "DialogCancelButton");
                cancel.Click += (_, __) => Close(); actions.Children.Add(cancel);
            }
            actions.Children.Add(primary);
            var footer = new Border { BorderBrush = ColorBrush("#DDE4EF"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 0, 16, 0), Child = actions };
            Grid.SetRow(footer, 2); layout.Children.Add(footer);
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        }
        private static SolidColorBrush ColorBrush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}

namespace BlueLink.Installation
{
    internal sealed class InstallerNoticeWindow : InstallerDialogWindow
    {
        internal InstallerNoticeWindow(string title, string message, Window owner = null) : base(title, message, owner) { }
    }
}
