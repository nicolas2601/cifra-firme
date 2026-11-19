namespace Reference.Payroll;

public enum WorkEventKind
{
    Worked,
    MedicalLeave,
    Absence,
    AdditionalDay
}

public readonly record struct WorkEvent(DateOnly Date, WorkEventKind Kind);

public sealed record PayrollEstimate(
    DateOnly Period,
    decimal MonthlySalary,
    decimal AbsenceAdjustment,
    decimal MedicalLeaveAdjustment,
    decimal AdditionalDayAdjustment,
    decimal Total,
    string RulesVersion);

public static class PayrollCalculator
{
    public static PayrollEstimate Calculate(DateOnly period, decimal monthlySalary, IEnumerable<WorkEvent> events)
    {
        if (period.Day != 1)
            throw new ArgumentException("El período debe empezar el primer día del mes.", nameof(period));
        if (monthlySalary <= 0 || monthlySalary != decimal.Truncate(monthlySalary))
            throw new ArgumentOutOfRangeException(nameof(monthlySalary), "El salario debe ser positivo y en pesos enteros.");
        ArgumentNullException.ThrowIfNull(events);

        var dates = new HashSet<DateOnly>();
        var absences = 0;
        var medicalLeaves = 0;
        var additionalDays = 0;

        foreach (var item in events)
        {
            if (item.Date.Year != period.Year || item.Date.Month != period.Month)
                throw new ArgumentException("Hay una novedad por fuera del período.", nameof(events));
            if (!dates.Add(item.Date))
                throw new ArgumentException("Solo se admite una novedad por persona y fecha.", nameof(events));

            switch (item.Kind)
            {
                case WorkEventKind.Worked: break;
                case WorkEventKind.Absence: absences++; break;
                case WorkEventKind.MedicalLeave: medicalLeaves++; break;
                case WorkEventKind.AdditionalDay: additionalDays++; break;
                default: throw new ArgumentOutOfRangeException(nameof(events), "Tipo de novedad desconocido.");
            }
        }

        var daysInMonth = DateTime.DaysInMonth(period.Year, period.Month);
        var absence = Round(-monthlySalary * absences / daysInMonth);
        var medicalLeave = Round(-monthlySalary * medicalLeaves / (3 * daysInMonth));
        var additionalDay = Round(monthlySalary * additionalDays * 1.25m / daysInMonth);

        return new(period, monthlySalary, absence, medicalLeave, additionalDay,
            monthlySalary + absence + medicalLeave + additionalDay, "demo-v1");
    }

    private static decimal Round(decimal amount) => decimal.Round(amount, 0, MidpointRounding.AwayFromZero);
}
