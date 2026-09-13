using System.Diagnostics;
using BlueLink.Domain;
using BlueLink.Localization;

namespace BlueLink.Updates;

public enum UpdateStage { Idle, Checking, Current, CheckFailed, Available, Downloading, DownloadFailed, Ready, Installing, HandedOff }

public sealed class UpdateWorkflow(UpdateService service) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cancellation;
    private int _operation;
    private bool _disposed;
    public UpdateStage Stage { get; private set; }
    public UpdateRelease? Release { get; private set; }
    public UpdatePackage? Package { get; private set; }
    public string Error { get; private set; } = "";
    public long DownloadedBytes { get; private set; }
    public double Percent => Release is { Size: > 0 } release ? Math.Clamp(100d * DownloadedBytes / release.Size, 0, 100) : 0;
    public bool Busy => Volatile.Read(ref _operation) != 0;
    public bool HasError => Error.Length > 0;
    public bool ShowProgress => Stage == UpdateStage.Downloading;
    public string CheckStatus => Stage switch
    {
        UpdateStage.Checking => Strings.Get("正在检查更新…"), UpdateStage.Current => Strings.Get("已是最新版本"),
        UpdateStage.CheckFailed => Strings.Get("检查失败，点击重试"), UpdateStage.Ready => Strings.Get("更新已准备就绪"),
        UpdateStage.Idle => "", _ => Release is null ? "" : Strings.Format($"发现新版本 {Release.Version}"),
    };
    public string Title => Strings.Get(Stage switch
    {
        UpdateStage.Downloading => "正在下载更新", UpdateStage.DownloadFailed => "更新下载失败",
        UpdateStage.Ready or UpdateStage.Installing => "更新已准备就绪", _ => "发现新版本",
    });
    public string Subtitle => Strings.Get(Stage switch
    {
        UpdateStage.Downloading => "正在下载安装包；不会在下载完成前修改当前版本。",
        UpdateStage.DownloadFailed => "下载失败不会影响当前已安装版本。",
        UpdateStage.Ready or UpdateStage.Installing => "安装程序将引导你完成更新。",
        _ => "已在应用内检测到新版本，请选择更新时间。",
    });
    public string VersionText => Stage == UpdateStage.DownloadFailed ? Strings.Get("未能完成下载") :
        Stage == UpdateStage.Downloading ? $"BlueLink {Release?.Version} · {Percent:0}%" :
        Stage is UpdateStage.Ready or UpdateStage.Installing ? Strings.Format($"BlueLink {Release?.Version} 下载完成") : Strings.Format($"BlueLink {Release?.Version} 已可用");
    public string SizeText => Stage switch
    {
        UpdateStage.Downloading => $"{DownloadedBytes / 1048576d:0.0} MB / {(Release?.Size ?? 0) / 1048576d:0.0} MB",
        UpdateStage.Ready or UpdateStage.Installing => Strings.Get("安装包已保存到本机"),
        UpdateStage.DownloadFailed => Strings.Get("未安装此更新"),
        _ => Strings.Format($"约 {(Release?.Size ?? 0) / 1048576d:0.0} MB · 安装前将核验发布者"),
    };
    public string NoteTitle => Strings.Get(Stage switch
    {
        UpdateStage.Downloading => "正在获取安装包", UpdateStage.DownloadFailed => "下载未完成",
        UpdateStage.Ready or UpdateStage.Installing => "准备安装", _ => "本次更新",
    });
    public string NoteText => Error.Length > 0 ? Error : Stage switch
    {
        UpdateStage.DownloadFailed => Error,
        UpdateStage.Downloading => Strings.Get("请保持网络连接，下载完成后可选择安装。"),
        UpdateStage.Ready or UpdateStage.Installing => Strings.Get("安装前请结束正在进行的传输。点击安装后将交由安装程序处理。"),
        _ => string.IsNullOrWhiteSpace(Release?.Notes) ? Strings.Get("此版本未提供更新说明。") : Release.Notes,
    };
    public string PrimaryText => Strings.Get(Stage switch
    {
        UpdateStage.Downloading => "下载中…", UpdateStage.DownloadFailed => "重试下载",
        UpdateStage.Ready => "立即安装", UpdateStage.Installing => "正在校验…", _ => "立即更新",
    });
    public string SecondaryText => Strings.Get(Stage == UpdateStage.Downloading ? "取消" : Stage == UpdateStage.DownloadFailed ? "关闭" : "稍后");

    public async Task CheckAsync()
    {
        if (!Begin()) return;
        Stage = UpdateStage.Checking; Error = ""; Notify();
        try
        {
            var release = await service.CheckAsync(UpdateService.CurrentVersion, _cancellation!.Token);
            Release = release; Package = null;
            Stage = release is null ? UpdateStage.Current : UpdateStage.Available;
        }
        catch (OperationCanceledException) when (_cancellation!.IsCancellationRequested) { Stage = UpdateStage.Idle; }
        catch (Exception error) { Stage = UpdateStage.CheckFailed; Error = Describe(error); }
        finally { End(); }
    }

    public async Task DownloadAsync()
    {
        if (Release is null || Stage is not (UpdateStage.Available or UpdateStage.DownloadFailed) || !Begin()) return;
        var release = Release;
        var operation = _cancellation;
        Stage = UpdateStage.Downloading; Error = ""; DownloadedBytes = 0; Package = null; Notify();
        var progress = new Progress<DownloadProgress>(value =>
        {
            if (Stage != UpdateStage.Downloading || _disposed || !ReferenceEquals(_cancellation, operation)) return;
            DownloadedBytes = value.Received; Notify();
        });
        try
        {
            Package = await service.DownloadAsync(release, progress, _cancellation!.Token);
            DownloadedBytes = release.Size; Stage = UpdateStage.Ready;
        }
        catch (OperationCanceledException) when (_cancellation!.IsCancellationRequested) { Stage = UpdateStage.Available; }
        catch (Exception error) { Stage = UpdateStage.DownloadFailed; Error = Describe(error); }
        finally { End(); }
    }

    public async Task<bool> InstallAsync(Func<bool> canInstall, Action<string>? launch = null)
    {
        if (Package is null || Stage != UpdateStage.Ready || !Begin()) return false;
        Stage = UpdateStage.Installing; Error = ""; Notify();
        try
        {
            if (!canInstall()) throw new InvalidOperationException("请先完成或取消正在进行的文件传输。");
            await using var lease = await service.AcquireVerifiedPackageAsync(Package, _cancellation!.Token);
            if (!canInstall()) throw new InvalidOperationException("请先完成或取消正在进行的文件传输。");
            _cancellation.Token.ThrowIfCancellationRequested();
            if (launch is null)
            {
                using var process = Process.Start(new ProcessStartInfo(Package.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Package.Path) });
                if (process is null) throw new IOException("无法启动安装程序，请重试。");
            }
            else launch(Package.Path);
            Stage = UpdateStage.HandedOff;
            return true;
        }
        catch (OperationCanceledException) when (_cancellation!.IsCancellationRequested) { Stage = UpdateStage.Ready; return false; }
        catch (Exception error)
        {
            Stage = error is InvalidDataException or FileNotFoundException or DirectoryNotFoundException ? UpdateStage.DownloadFailed : UpdateStage.Ready;
            Error = Describe(error); return false;
        }
        finally { End(); }
    }

    public void Cancel() => _cancellation?.Cancel();
    public void RefreshText() => Notify();
    private bool Begin()
    {
        if (_disposed || Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return false;
        _cancellation = new(); return true;
    }
    private void End() { _cancellation?.Dispose(); _cancellation = null; Interlocked.Exchange(ref _operation, 0); Notify(); }
    private void Notify() => Raise(string.Empty);
    private static string Describe(Exception error) => error is OperationCanceledException ? Strings.Get("网络请求超时，请重试。") :
        error is System.Net.Http.HttpRequestException ? Strings.Get("无法连接更新服务器，请检查网络后重试。") : Strings.Get(error.Message);
    public void Dispose() { if (_disposed) return; _disposed = true; Cancel(); service.Dispose(); }
}
