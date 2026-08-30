using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace BlueLink;

public enum BlueLinkDialogTone { Information, Warning, Error }

public static class BlueLinkDialog
{
    public static void Show(Window? owner, string title, string message, BlueLinkDialogTone tone = BlueLinkDialogTone.Information) =>
        ShowContent(owner, title, CreateMessageContent(message, tone), confirmation: false, tone);

    public static bool Confirm(Window? owner, string title, string message, BlueLinkDialogTone tone = BlueLinkDialogTone.Warning) =>
        ShowContent(owner, title, CreateMessageContent(message, tone), confirmation: true, tone);

    internal static bool ConfirmContent(Window? owner, string title, FrameworkElement content,
        string primaryButtonText = "确认", string closeButtonText = "取消",
        BlueLinkDialogTone tone = BlueLinkDialogTone.Warning) =>
        ShowContent(owner, title, content, confirmation: true, tone,
            primaryButtonText, closeButtonText);

    private static bool ShowContent(Window? owner, string title, FrameworkElement content,
        bool confirmation, BlueLinkDialogTone tone,
        string? primaryButtonText = null, string? closeButtonText = null)
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            ShowTitle = true,
            Content = content,
            PrimaryButtonText = primaryButtonText ?? (confirmation ? "确认" : "确定"),
            CloseButtonText = closeButtonText ?? (confirmation ? "取消" : string.Empty),
            PrimaryButtonAppearance = tone switch
            {
                BlueLinkDialogTone.Error => ControlAppearance.Danger,
                BlueLinkDialogTone.Warning => ControlAppearance.Caution,
                _ => ControlAppearance.Primary,
            },
            CloseButtonAppearance = ControlAppearance.Secondary,
            WindowStartupLocation = owner is { IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 500,
            MaxWidth = 660,
            ShowInTaskbar = false,
            FocusVisualStyle = null,
        };
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.Style = Application.Current.TryFindResource(
            typeof(Wpf.Ui.Controls.MessageBox)) as Style ??
            throw new InvalidOperationException("The official WPF UI MessageBox style is unavailable.");

        var overlay = OwnerOverlay.TryCreate(owner);
        try
        {
            var result = ShowDialogSynchronously(dialog);
            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }
        finally
        {
            overlay?.Dispose();
        }
    }

    private static Wpf.Ui.Controls.MessageBoxResult ShowDialogSynchronously(
        Wpf.Ui.Controls.MessageBox dialog)
    {
        var task = dialog.ShowDialogAsync();
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ =>
            dialog.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static FrameworkElement CreateMessageContent(string message, BlueLinkDialogTone tone)
    {
        var iconForeground = Brush(tone switch
        {
            BlueLinkDialogTone.Warning => "#B06A00",
            BlueLinkDialogTone.Error => "#D92D20",
            _ => "#176BFF",
        });
        var iconBackground = Brush(tone switch
        {
            BlueLinkDialogTone.Warning => "#FFF4E5",
            BlueLinkDialogTone.Error => "#FDECEC",
            _ => "#EAF1FF",
        });
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 4), MaxWidth = 560 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Background = iconBackground,
            Child = new TextBlock
            {
                Text = tone == BlueLinkDialogTone.Error ? "×" :
                    tone == BlueLinkDialogTone.Warning ? "!" : "i",
                Foreground = iconForeground,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            Foreground = Brush("#162033"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            MaxWidth = 470,
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(icon);
        grid.Children.Add(body);
        return grid;
    }

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));

    private sealed class OwnerOverlay : IDisposable
    {
        private readonly Panel _parent;
        private readonly Border _overlay;

        private OwnerOverlay(Panel parent, Border overlay)
        {
            _parent = parent;
            _overlay = overlay;
        }

        public static OwnerOverlay? TryCreate(Window? owner)
        {
            if (owner is not { IsVisible: true } || owner.Content is not Panel parent)
                return null;

            var overlay = new Border
            {
                Background = Brush("#660A1020"),
                IsHitTestVisible = false,
            };
            Panel.SetZIndex(overlay, int.MaxValue);
            if (parent is Grid)
            {
                Grid.SetRowSpan(overlay, int.MaxValue);
                Grid.SetColumnSpan(overlay, int.MaxValue);
            }
            parent.Children.Add(overlay);
            return new OwnerOverlay(parent, overlay);
        }

        public void Dispose()
        {
            if (_parent.Children.Contains(_overlay))
                _parent.Children.Remove(_overlay);
        }
    }
}
