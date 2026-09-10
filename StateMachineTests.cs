using MailLoadTester;
using Xunit;

namespace MailLoadTester.Tests;

public class StateMachineTests
{
    [Fact]
    public void NormalLifecycle_IsAllowed()
    {
        var fsm = new TestStateMachine();

        Assert.True(fsm.Transition(TestPhase.Validating, "test"));
        Assert.True(fsm.Transition(TestPhase.PreparingAttachments, "test"));
        Assert.True(fsm.Transition(TestPhase.ConnectingSmtp, "test"));
        Assert.True(fsm.Transition(TestPhase.Sending, "test"));
        Assert.True(fsm.Transition(TestPhase.BatchPause, "test"));
        Assert.True(fsm.Transition(TestPhase.Sending, "test"));
        Assert.True(fsm.Transition(TestPhase.Completed, "test"));
        Assert.Equal(TestPhase.Completed, fsm.Phase);
    }

    [Fact]
    public void InvalidTransition_IsRejected()
    {
        var fsm = new TestStateMachine();
        Assert.False(fsm.Transition(TestPhase.Sending, "skip"));
        Assert.Equal(TestPhase.Idle, fsm.Phase);
    }

    [Fact]
    public void Cancellation_IsAllowedFromRunningState()
    {
        var fsm = new TestStateMachine();
        fsm.Transition(TestPhase.Validating, "test");
        fsm.Transition(TestPhase.PreparingAttachments, "test");
        fsm.Transition(TestPhase.Sending, "dry-run");

        Assert.True(fsm.Transition(TestPhase.Cancelled, "STOP"));
        Assert.Equal(TestPhase.Cancelled, fsm.Phase);
        Assert.False(fsm.Transition(TestPhase.Sending, "must not resume"));
    }

    [Theory]
    [InlineData(MessageStep.Queued, "Fronta")]
    [InlineData(MessageStep.WaitingRateLimit, "Rate limit")]
    [InlineData(MessageStep.RentingConnection, "SMTP spojení")]
    [InlineData(MessageStep.BuildingMime, "MIME")]
    [InlineData(MessageStep.SmtpSend, "SMTP SEND")]
    [InlineData(MessageStep.Succeeded, "OK")]
    [InlineData(MessageStep.FailedTransient, "4xx / retry")]
    [InlineData(MessageStep.FailedFinal, "FINÁLNÍ CHYBA")]
    public void MessageStepText_IsStable(MessageStep step, string expected)
        => Assert.Equal(expected, TestStateMachine.MessageText(step));
}

