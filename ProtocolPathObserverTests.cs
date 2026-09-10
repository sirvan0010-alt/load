using Xunit;

namespace MailLoadTester.Tests;

public sealed class ProtocolPathObserverTests
{
    [Fact]
    public async Task SeparateObservers_DoNotMixParallelSmtpStreams()
    {
        var first = new ProtocolPathObserver();
        var second = new ProtocolPathObserver();

        await Task.WhenAll(
            Task.Run(() =>
            {
                first.LogClient(Array.Empty<byte>(), 0, 0);
                first.LogClient(System.Text.Encoding.ASCII.GetBytes("EHLO first\r\n"), 0, 12);
                first.LogServer(System.Text.Encoding.ASCII.GetBytes("250 hello\r\n"), 0, 12);
            }),
            Task.Run(() =>
            {
                second.LogClient(System.Text.Encoding.ASCII.GetBytes("MAIL FROM:<second@example.test>\r\n"), 0, 33);
                second.LogServer(System.Text.Encoding.ASCII.GetBytes("550 denied\r\n"), 0, 12);
            }));

        var firstEvents = first.Drain();
        var secondEvents = second.Drain();

        Assert.Contains(firstEvents, e => e.Step == DeliveryStepKind.Ehlo && e.Ok == true);
        Assert.DoesNotContain(firstEvents, e => e.Step == DeliveryStepKind.MailFrom);
        Assert.Contains(secondEvents, e => e.Step == DeliveryStepKind.MailFrom && e.Ok == false);
        Assert.DoesNotContain(secondEvents, e => e.Step == DeliveryStepKind.Ehlo);
    }

    [Fact]
    public async Task SameObserver_CrossThreadClientAndServerStillPairCorrectly()
    {
        var observer = new ProtocolPathObserver();
        var clientReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverGo = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var clientTask = Task.Run(() =>
        {
            observer.LogClient(System.Text.Encoding.ASCII.GetBytes("MAIL FROM:<a@example.test>\r\n"), 0, 30);
            clientReady.SetResult(true);
        });

        var serverTask = Task.Run(async () =>
        {
            await clientReady.Task;
            await serverGo.Task;
            observer.LogServer(System.Text.Encoding.ASCII.GetBytes("550 denied\r\n"), 0, 12);
        });

        await clientTask;
        serverGo.SetResult(true);
        await serverTask;

        var events = observer.Drain();
        Assert.Contains(events, e => e.Step == DeliveryStepKind.MailFrom && e.Ok == false);
        Assert.Contains(events, e => e.Step == DeliveryStepKind.Error && e.Ok == false);
    }

}
