using System.Windows.Threading;
using BlueLink.Domain;

namespace BlueLink;

internal static partial class DesktopAcceptance
{
    private static void ScheduleIncomingMessageFixture(MainWindow window)
    {
        // One predetermined in-memory arrival after startup. Native acceptance
        // scrolls and clicks normally; this fixture never drives the live UI.
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(40)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.ViewModel.Messages.Add(new ChatItem(Guid.NewGuid(),
                "QA 新到达消息：用于核验阅读历史时保留当前位置，以及点击提示后跳至最新消息。",
                false, DateTimeOffset.Now, MessageStatus.Received));
        };
        window.Closed += (_, _) => timer.Stop();
        timer.Start();
    }
}
