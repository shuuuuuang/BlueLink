using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BlueLink;
using BlueLink.Domain;

internal sealed partial class OffscreenWpfVerification
{
    private void CaptureFileShowcase(string directory, string output)
    {
        // Isolated visual fixture: no production database, native window, clipboard or transfer.
        var main = new MainWindow(false, directory);
        try
        {
            WaitForUiTask(DesktopAcceptance.InitializeAsync(main, directory, "message-history"));
            var download = Path.Combine(directory, "接收文件");
            Directory.CreateDirectory(download);
            WaitForUiTask(main.ViewModel.SaveSettingsAsync(main.ViewModel.Settings with {
                Theme = "light", Language = "zh-CN", DownloadDirectory = download }));
            var source = Path.Combine(directory, "QA-source.txt");
            File.WriteAllText(source, "Screenshot fixture only");
            main.ViewModel.AllTransfers.Clear();
            var date = new DateTimeOffset(2026, 9, 14, 18, 0, 0, TimeSpan.FromHours(8));
            var peer = main.ViewModel.Conversations.First();
            for (var i = 1; i <= 250; i++)
            {
                var photo = i % 3 == 0;
                var size = (long)(251 - i) * 65536;
                main.ViewModel.AllTransfers.Add(new TransferItem {
                    Id = Guid.Parse($"00000000-0000-0000-0000-{i:x12}"),
                    Name = photo ? $"项目现场照片_{i:D3}.jpg" : $"项目交付资料_{i:D3}.pdf",
                    MimeType = photo ? "image/jpeg" : "application/pdf",
                    TotalBytes = size, CompletedBytes = size, Status = TransferStatus.Completed,
                    Outgoing = i % 2 == 0, PeerId = peer.PeerId, PeerName = "QA 测试手机",
                    LocalPath = source, CreatedAt = date.AddMinutes(-i)
                });
            }
            main.OpenFileWorkspace(true);
            var root = DetachForRendering(main);
            var list = (ListBox)main.FindName("TransferList");
            const int width = 1440, height = 900;
            void Shot(string name) => Capture(root, output, name, width, height);
            void Click(string name) => (main.FindName(name) is FileTableHeader header ? header.SortButton : (System.Windows.Controls.Button)main.FindName(name))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            SetFileColumn(main,"Kind","Images");
            SetFileColumn(main,"Route","direction:Incoming");
            SetFileColumn(main,"Status","Completed");
            Check(list.Items.Count == 42 && list.Items.Cast<TransferItem>().All(item => !item.Outgoing && item.Name.EndsWith(".jpg")), "showcase filters actual received images");
            Shot("01-filter-received-images");

            typeof(MainWindow).GetMethod("FileClear_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, [main, new RoutedEventArgs()]);
            Click("FileSizeHeader");
            Check(list.Items.Count == 100 && ((TransferItem)list.Items[0]).TotalBytes == 250L * 65536, "showcase largest-first column sort");
            Shot("02-sort-size-descending");

            Click("FileMoreButton");
            Check(list.Items.Count == 200 && ((FrameworkElement)main.FindName("FileMoreButton")).Visibility == Visibility.Visible, "showcase loads 200 of 250 records and keeps more action");
            Shot("03-pagination-200-of-250");

            main.SetFileSelectionMode(true);
            foreach (var item in list.Items.Cast<TransferItem>().Take(3).ToArray()) list.SelectedItems.Add(item);
            Check(list.SelectedItems.Count == 3, "showcase batch selection");
            Shot("04-batch-three-selected");
            main.SetFileSelectionMode(false);
            void EditorShot(string column,string headerName,string fileName,Action<FileFilterEditor>? edit=null)
            {
                Layout(root,width,height);
                var header=(FileTableHeader)main.FindName(headerName);
                var editor=main.CreateFileColumnEditor(column); edit?.Invoke(editor);
                editor.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                var point=header.FilterButton.TranslatePoint(new Point(header.FilterButton.ActualWidth,header.FilterButton.ActualHeight),root);
                var host=new Grid(); host.Children.Add(root);
                editor.HorizontalAlignment=HorizontalAlignment.Left; editor.VerticalAlignment=VerticalAlignment.Top;
                editor.Margin=new Thickness(Math.Max(8,point.X-editor.DesiredSize.Width),point.Y+4,0,0);
                host.Children.Add(editor); header.SetFilterOpen(true);
                try { Capture(host,output,fileName,width,height); }
                finally { host.Children.Clear(); header.SetFilterOpen(false); }
            }
            EditorShot("Status","FileStatusHeader","05-checkbox-filter",editor=> {
                editor.Choices["Completed"].IsChecked=true; editor.Choices["Failed"].IsChecked=true;
            });
            EditorShot("Date","FileTimeHeader","06-date-range",editor=> {
                editor.StartDate.SelectedDate=new DateTime(2026,9,1); editor.EndDate.SelectedDate=new DateTime(2026,9,14);
            });
            Check(new System.Windows.Interop.WindowInteropHelper(main).Handle == IntPtr.Zero, "showcase creates no desktop HWND");
        }
        finally { WaitForUiTask(main.DisposeAsync().AsTask()); }
    }
}
