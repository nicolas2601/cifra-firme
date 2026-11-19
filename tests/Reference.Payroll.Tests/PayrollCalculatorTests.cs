namespace Reference.Payroll.Tests;

public class PayrollCalculatorTests
{
    private static readonly DateOnly Period = new(2026, 4, 1);

    [Fact]
    public void ExplainsEachAdjustmentAndAddsUpToTheTotal()
    {
        var estimate = PayrollCalculator.Calculate(Period, 1_800_000m,
        [
            new(Period.AddDays(1), WorkEventKind.Absence),
            new(Period.AddDays(2), WorkEventKind.Absence),
            new(Period.AddDays(3), WorkEventKind.MedicalLeave),
            new(Period.AddDays(4), WorkEventKind.MedicalLeave),
            new(Period.AddDays(5), WorkEventKind.MedicalLeave),
            new(Period.AddDays(6), WorkEventKind.AdditionalDay),
            new(Period.AddDays(7), WorkEventKind.Worked)
        ]);

        Assert.Equal(1_800_000m, estimate.MonthlySalary);
        Assert.Equal(-120_000m, estimate.AbsenceAdjustment);
        Assert.Equal(-60_000m, estimate.MedicalLeaveAdjustment);
        Assert.Equal(75_000m, estimate.AdditionalDayAdjustment);
        Assert.Equal(1_695_000m, estimate.Total);
        Assert.Equal("demo-v1", estimate.RulesVersion);
    }

    [Fact]
    public void NoEvents_PreservesMonthlySalary()
        => Assert.Equal(1_800_000m, PayrollCalculator.Calculate(Period, 1_800_000m, []).Total);

    [Fact]
    public void UsesActualCalendarDaysAndRoundsWholePesos()
    {
        var february = new DateOnly(2028, 2, 1);
        var estimate = PayrollCalculator.Calculate(february, 1_000_000m,
            [new(february, WorkEventKind.Absence)]);
        Assert.Equal(-34_483m, estimate.AbsenceAdjustment);
    }

    [Fact]
    public void RoundsAConceptOnceAfterCountingItsDays()
    {
        var estimate = PayrollCalculator.Calculate(Period, 1_800_002m,
            Enumerable.Range(0, 8).Select(day => new WorkEvent(Period.AddDays(day), WorkEventKind.Absence)));
        Assert.Equal(-480_001m, estimate.AbsenceAdjustment);
    }

    [Fact]
    public void FullMonthOfAbsencesLeavesZero()
    {
        var estimate = PayrollCalculator.Calculate(Period, 1_800_001m,
            Enumerable.Range(0, 30).Select(day => new WorkEvent(Period.AddDays(day), WorkEventKind.Absence)));
        Assert.Equal(0m, estimate.Total);
    }

    [Theory]
    [InlineData(WorkEventKind.MedicalLeave)]
    [InlineData(WorkEventKind.Absence)]
    public void RejectsDuplicateOrIncompatibleEventsOnTheSameDate(WorkEventKind second)
        => Assert.Throws<ArgumentException>(() => PayrollCalculator.Calculate(Period, 1_800_000m,
            [new(Period, WorkEventKind.MedicalLeave), new(Period, second)]));

    [Fact]
    public void RejectsAnEventOutsideThePeriod()
        => Assert.Throws<ArgumentException>(() => PayrollCalculator.Calculate(Period, 1_800_000m,
            [new(Period.AddMonths(-1), WorkEventKind.Absence)]));

    [Fact]
    public void RejectsAnUnknownEventType()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PayrollCalculator.Calculate(Period, 1_800_000m,
            [new(Period, (WorkEventKind)99)]));

    [Fact]
    public void RejectsPeriodThatDoesNotStartOnTheFirstDay()
        => Assert.Throws<ArgumentException>(() => PayrollCalculator.Calculate(Period.AddDays(1), 1_800_000m, []));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void RejectsNonPositiveOrFractionalSalary(decimal salary)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PayrollCalculator.Calculate(Period, salary, []));
}
