using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using BlueLink.Domain;
using Wpf.Ui.Controls;

namespace BlueLink;

public partial class DeviceNameWindow : FluentWindow
{
    private readonly MainViewModel _model;
    private bool _saving;
    public DeviceNameWindow(MainViewModel model)
    {
        _model = model;
        InitializeComponent();
        DeviceNameInput.Text = model.LocalDeviceDisplayName;
        Loaded += (_, _) => { DeviceNameInput.Focus(); DeviceNameInput.SelectAll(); };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        if (LocalDeviceName.ValidationError(DeviceNameInput.Text) is { } error)
        {
            ShowError(error);
            DeviceNameInput.Focus();
            return;
        }
        _saving = true;
        DeviceNameInput.IsEnabled = SaveNameButton.IsEnabled = CancelNameButton.IsEnabled = false;
        try
        {
            await _model.SaveSettingsAsync(_model.Settings with { LocalDeviceName = DeviceNameInput.Text.Trim() });
            _saving = false;
            DialogResult = true;
        }
        catch (Exception failure) { ShowError(Localization.Strings.Format($"保存失败，名称已保留，可重试。{failure.Message}")); }
        finally
        {
            _saving = false;
            DeviceNameInput.IsEnabled = SaveNameButton.IsEnabled = CancelNameButton.IsEnabled = true;
        }
    }

    private void ShowError(string message) { NameStatus.Title = Localization.Strings.Get(message); NameStatus.IsOpen = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_saving) DialogResult = false; }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_saving) e.Cancel = true; }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_saving) { e.Handled = true; DialogResult = false; }
    }
}
