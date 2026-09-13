using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlueLink;

public enum BlueLinkDialogTone { Information, Warning, Error }

public static class BlueLinkDialog
{
    internal static IDisposable? DimOwner(Window owner, string color, double topInset) =>
        OwnerOverlay.TryCreate(owner, color, topInset);

    public static void Show(Window? owner, string title, string message, BlueLinkDialogTone tone = BlueLinkDialogTone.Information) =>
        Present(owner, CreateWindow(title, message, tone));

    public static bool Confirm(Window? owner, string title, string message, BlueLinkDialogTone tone = BlueLinkDialogTone.Warning) =>
        Present(owner, CreateWindow(title, message, tone, confirmation: true)) == Wpf.Ui.Controls.MessageBoxResult.Primary;

    internal static bool ConfirmContent(Window? owner, string title, FrameworkElement content,
        string primaryButtonText = "确认", string closeButtonText = "取消",
        BlueLinkDialogTone tone = BlueLinkDialogTone.Warning) =>
        Present(owner, CreateWindow(title, string.Empty, tone, true, primaryButtonText, closeButtonText, options: content))
            == Wpf.Ui.Controls.MessageBoxResult.Primary;

    internal static Wpf.Ui.Controls.MessageBoxResult AskSaveSettings(Window owner) =>
        Present(owner, CreateWindow("设置尚未保存", "是否保存本次设置修改后返回？", BlueLinkDialogTone.Warning,
            true, "保存并返回", "继续编辑", "放弃修改"));

    // All notice, confirmation and three-choice callers share the reviewed confirmation shell.
    // Error severity colors the icon; acknowledging an error is not a destructive action.
    internal static ConfirmationWindow CreateWindow(string title, string message, BlueLinkDialogTone tone,
        bool confirmation = false, string? primaryText = null, string? cancelText = null,
        string? secondaryText = null, UIElement? options = null) =>
        new(new ConfirmationDocument(Localization.Strings.Get(title), Localization.Strings.Get(message), string.Empty,
            Localization.Strings.Get(primaryText ?? (confirmation ? "确认" : "确定")),
            Destructive: confirmation && tone == BlueLinkDialogTone.Error,
            CancelText: Localization.Strings.Get(cancelText ?? (confirmation ? "取消" : string.Empty)),
            SecondaryText: secondaryText is null ? null : Localization.Strings.Get(secondaryText), Tone: tone), options) { MinHeight = Presentation.WindowsDialogLayout.NoticeMinHeight };

    private static Wpf.Ui.Controls.MessageBoxResult Present(Window? owner, ConfirmationWindow dialog)
    {
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.MaxHeight = Math.Min(dialog.MaxHeight, Math.Max(250, owner.ActualHeight - 40));
        }
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using var overlay = OwnerOverlay.TryCreate(owner, "#3D0F172A", owner is MainWindow ? 52 : 0);
        dialog.ShowDialog();
        return dialog.Result;
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

        public static OwnerOverlay? TryCreate(Window? owner, string color = "#660A1020", double topInset = 0)
        {
            if (owner is not { IsVisible: true } || owner.Content is not Panel parent)
                return null;

            var overlay = new Border
            {
                Background = Brush(color),
                Margin = new Thickness(0, topInset, 0, 0),
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
