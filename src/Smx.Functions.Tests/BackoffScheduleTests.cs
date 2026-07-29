using Smx.Functions.Sds.Data;
using Xunit;

public class BackoffScheduleTests
{
    // 1, 2, 4, 8, 16, then pinned at 32. The cap is what makes this a schedule rather than an
    // abandonment: a substance whose supplier is down for a year still gets a monthly attempt.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 32)]
    [InlineData(7, 32)]
    [InlineData(40, 32)]
    public void Delay_doubles_then_pins_at_the_cap(int attemptCount, int expectedDays)
        => Assert.Equal(expectedDays, BackoffSchedule.DelayDays(attemptCount));

    // Attempt 0 is not a real input, but a caller that increments in the wrong order must not get a
    // zero or negative delay and retry in a hot loop.
    [Fact]
    public void A_delay_is_never_less_than_a_day()
    {
        Assert.Equal(1, BackoffSchedule.DelayDays(0));
        Assert.Equal(1, BackoffSchedule.DelayDays(-5));
    }

    [Fact]
    public void Next_attempt_is_the_delay_after_the_last_attempt()
        => Assert.Equal(
            DateTimeOffset.Parse("2026-08-02T03:00:00Z"),
            BackoffSchedule.NextAttemptUtc(DateTimeOffset.Parse("2026-07-29T03:00:00Z"), 3));

    // Overflow guard: 1 << 30 is still a valid int but a nonsense delay, and a shift past 31 is
    // undefined. The cap has to bite before the arithmetic does.
    [Fact]
    public void A_pathological_attempt_count_still_yields_the_cap()
        => Assert.Equal(BackoffSchedule.MaxDelayDays, BackoffSchedule.DelayDays(int.MaxValue));
}
