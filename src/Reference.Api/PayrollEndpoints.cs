using System.Globalization;
using System.Text;
using System.Text.Json;
using Reference.Payroll;
using Microsoft.EntityFrameworkCore;
using static Reference.Api.Access;
using static Reference.Api.Administration;

namespace Reference.Api;

internal static class PayrollEndpoints
{
    public static void MapPayroll(this WebApplication app)
    {
        app.MapGet("/api/crew/people", CrewPeople).RequireAuthorization();
        var events = app.MapGroup("/api/people/{id:guid}/events").RequireAuthorization();
        events.MapGet("", ListEvents);
        events.MapGet("/{date}", GetEvent);
        events.MapPost("", (Guid id, EventInput input, AppDb db, HttpContext http, CancellationToken ct) =>
            input.Date is null || input.Kind is null
                ? Task.FromResult(Bad("La fecha y el tipo son obligatorios."))
                : SaveEvent(id, input.Date.Value, input.Kind, EventOperation.Create, db, http, ct));
        events.MapPut("/{date}", (Guid id, string date, EventInput input, AppDb db, HttpContext http, CancellationToken ct) =>
            !TryDate(date, out var day) || input.Kind is null || (input.Date is not null && input.Date != day)
                ? Task.FromResult(Bad("La fecha o tipo de novedad no coincide."))
                : SaveEvent(id, day, input.Kind, EventOperation.Update, db, http, ct));
        events.MapDelete("/{date}", (Guid id, string date, AppDb db, HttpContext http, CancellationToken ct) =>
            TryDate(date, out var day) ? SaveEvent(id, day, null, EventOperation.Delete, db, http, ct) : Task.FromResult(Bad("Fecha inválida.")));

        var payroll = app.MapGroup("/api/payroll").RequireAuthorization();
        payroll.MapGet("", History);
        payroll.MapGet("/{month}/preview", Preview);
        payroll.MapPost("/{month}/close", Close);
        payroll.MapGet("/{month}", ClosedSummary);
        payroll.MapGet("/{month}/people/{id:guid}", GetReceipt);
        payroll.MapGet("/{month}/export", Export);
    }

    private static bool CanEditEvents(Person actor, Person person) =>
        actor.Has(Permission.EventsAll) ||
        (actor.Has(Permission.EventsCrew) && actor.Crew is not null && actor.Crew == person.Crew);

    private static async Task<IResult> CrewPeople(AppDb db, HttpContext http, CancellationToken ct)
    {
        var actor = Actor(http);
        if (!actor.Any(Permission.EventsAll | Permission.EventsCrew)) return Results.Forbid();
        var people = db.People.AsNoTracking().Where(x => x.Active && x.Salary > 0);
        if (!actor.Has(Permission.EventsAll))
            people = people.Where(x => actor.Crew != null && x.Crew == actor.Crew);
        var rows = await people.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.Crew }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> ListEvents(Guid id, string month, AppDb db, HttpContext http, CancellationToken ct)
    {
        var actor = Actor(http);
        if (!actor.Any(Permission.EventsAll | Permission.EventsCrew)) return Results.Forbid();
        if (!TryPeriod(month, out var period)) return Bad("Usa un período con formato AAAA-MM.");
        var person = await db.People.FindAsync([id], ct);
        if (person is null) return Results.NotFound();
        if (!CanEditEvents(actor, person)) return Results.Forbid();
        var rows = await ForPeriod(db, period).Where(x => x.PersonId == id).OrderBy(x => x.Date).ToListAsync(ct);
        return Results.Ok(rows.Select(x => new { x.Date, x.Kind }));
    }

    private static async Task<IResult> GetEvent(Guid id, string date, AppDb db, HttpContext http, CancellationToken ct)
    {
        var actor = Actor(http);
        if (!actor.Any(Permission.EventsAll | Permission.EventsCrew)) return Results.Forbid();
        if (!TryDate(date, out var day)) return Bad("Fecha inválida.");
        var person = await db.People.FindAsync([id], ct);
        if (person is null) return Results.NotFound();
        if (!CanEditEvents(actor, person)) return Results.Forbid();
        var item = await db.Events.FindAsync([id, day], ct);
        return item is null ? Results.NotFound() : Results.Ok(new { item.Date, item.Kind });
    }

    private static async Task<IResult> SaveEvent(Guid id, DateOnly date, WorkEventKind? kind, EventOperation operation, AppDb db,
        HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Any(Permission.EventsAll | Permission.EventsCrew)) return Results.Forbid();
        if (kind is { } type && !Enum.IsDefined(type)) return Bad("Tipo de novedad inválido.");
        await using var tx = await db.LockAsync(ct);
        var actor = await ReloadActor(db, Actor(http), ct);
        if (actor is null || !actor.Any(Permission.EventsAll | Permission.EventsCrew)) return Results.Forbid();
        var person = await db.People.FindAsync([id], ct);
        if (person is null) return Results.NotFound();
        if (!CanEditEvents(actor, person)) return Results.Forbid();
        if (await db.Closures.AnyAsync(x => x.Period == new DateOnly(date.Year, date.Month, 1), ct))
            return Conflict("El período ya está cerrado.");
        if (operation != EventOperation.Delete && (!person.Active || person.Salary <= 0))
            return Conflict("La persona no está activa o no tiene salario liquidable.");
        if (operation == EventOperation.Delete && !actor.Has(Permission.EventsAll) && (!person.Active || person.Salary <= 0))
            return Results.Forbid();
        var entry = await db.Events.FindAsync([id, date], ct);
        if (operation == EventOperation.Create && entry is not null) return Conflict("Ya existe una novedad para esta persona y fecha.");
        if (operation != EventOperation.Create && entry is null) return Results.NotFound();
        if (operation == EventOperation.Create)
        {
            entry = new Attendance { PersonId = id, Date = date, Kind = kind!.Value };
            db.Events.Add(entry);
        }
        else if (operation == EventOperation.Delete) db.Events.Remove(entry!);
        else entry!.Kind = kind!.Value;

        var path = $"/api/people/{id}/events/{date:yyyy-MM-dd}";
        await db.CommitAsync(tx, actor.Id, $"event.{operation.ToString().ToLowerInvariant()}", $"{id}/{date:yyyy-MM-dd}", true, ct);
        return operation switch
        {
            EventOperation.Create => Results.Created(path, new { entry!.Date, entry.Kind }),
            EventOperation.Update => Results.Ok(new { entry!.Date, entry.Kind }),
            _ => Results.NoContent()
        };
    }

    private static async Task<IResult> Preview(string month, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.PayrollClose)) return Results.Forbid();
        if (!TryPeriod(month, out var period)) return Bad("Usa un período con formato AAAA-MM.");
        await using var tx = await db.LockAsync(ct);
        var actor = await ReloadActor(db, Actor(http), ct);
        if (actor is null || !actor.Has(Permission.PayrollClose)) return Results.Forbid();
        if (await db.Closures.AnyAsync(x => x.Period == period, ct)) return Conflict("Consulta el histórico del período cerrado.");
        var revision = await db.RevisionAsync(ct);
        var receipts = await Calculate(db, period, ct);
        var blockedPeople = await BlockedPeople(db, period, ct);
        return Results.Ok(new { period, revision, total = receipts.Sum(x => x.Total), people = receipts.Select(ReceiptView), blockedPeople });
    }

    private static async Task<IResult> Close(string month, CloseInput input, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.PayrollClose)) return Results.Forbid();
        if (!TryPeriod(month, out var period) || input.Revision is null or < 0) return Bad("Período o revisión inválidos.");
        await using var tx = await db.LockAsync(ct);
        var actor = await ReloadActor(db, Actor(http), ct);
        if (actor is null || !actor.Has(Permission.PayrollClose)) return Results.Forbid();
        var current = await db.Closures.Include(x => x.Receipts).SingleOrDefaultAsync(x => x.Period == period, ct);
        if (current is not null)
            return current.Revision == input.Revision ? Results.Ok(Summary(current)) : Conflict("El período ya fue cerrado con otra revisión.");
        if (input.Revision != await db.RevisionAsync(ct)) return Conflict("Los datos cambiaron: vuelve a revisar el cálculo.");
        if ((await BlockedPeople(db, period, ct)).Count != 0)
            return Conflict("El período tiene novedades de personas sin salario o inactivas.");
        var receipts = await Calculate(db, period, ct);
        if (receipts.Count == 0) return Conflict("No hay personas liquidables en el período.");
        var closure = new ClosedPeriod
        {
            Period = period,
            ClosedAt = DateTimeOffset.UtcNow,
            ClosedBy = actor.Id,
            Revision = input.Revision.Value,
            RulesVersion = receipts[0].RulesVersion,
            Receipts = receipts
        };
        db.Closures.Add(closure);
        await db.CommitAsync(tx, actor.Id, "payroll.closed", month, false, ct);
        return Results.Created($"/api/payroll/{month}", Summary(closure));
    }

    private static async Task<IResult> History(AppDb db, HttpContext http, CancellationToken ct)
    {
        var actor = Actor(http);
        if (actor.Has(Permission.PayrollReadAll))
        {
            var rows = await db.Closures.AsNoTracking().OrderByDescending(x => x.Period).Take(60)
                .Select(x => new { x.Period, x.ClosedAt, x.RulesVersion, Total = x.Receipts.Sum(r => r.Total) }).ToListAsync(ct);
            return Results.Ok(rows);
        }
        if (!actor.Has(Permission.PayrollReadOwn)) return Results.Forbid();
        var own = await db.Receipts.AsNoTracking().Where(x => x.PersonId == actor.Id).OrderByDescending(x => x.Period).Take(60)
            .Select(x => new { x.Period, x.Total, x.RulesVersion }).ToListAsync(ct);
        return Results.Ok(own);
    }

    private static async Task<IResult> ClosedSummary(string month, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.PayrollReadAll)) return Results.Forbid();
        if (!TryPeriod(month, out var period)) return Bad("Período inválido.");
        var close = await db.Closures.AsNoTracking().Include(x => x.Receipts).SingleOrDefaultAsync(x => x.Period == period, ct);
        return close is null ? Results.NotFound() : Results.Ok(new
        {
            summary = Summary(close),
            people = close.Receipts.OrderBy(x => x.PersonId).Select(ReceiptView)
        });
    }

    private static async Task<IResult> GetReceipt(string month, Guid id, AppDb db, HttpContext http, CancellationToken ct)
    {
        var actor = Actor(http);
        if (!actor.Has(Permission.PayrollReadAll) && (!actor.Has(Permission.PayrollReadOwn) || actor.Id != id)) return Results.Forbid();
        if (!TryPeriod(month, out var period)) return Bad("Período inválido.");
        var row = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(x => x.Period == period && x.PersonId == id, ct);
        return row is null ? Results.NotFound() : Results.Ok(ReceiptView(row));
    }

    private static async Task<IResult> Export(string month, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.PayrollExport)) return Results.Forbid();
        if (!TryPeriod(month, out var period)) return Bad("Período inválido.");
        if (!await db.Closures.AnyAsync(x => x.Period == period, ct)) return Results.NotFound();
        var rows = await db.Receipts.AsNoTracking().Where(x => x.Period == period).OrderBy(x => x.DisplayName).ThenBy(x => x.PersonId).ToListAsync(ct);
        var csv = new StringBuilder("periodo,persona,salario,ausencias,incapacidades,adicionales,total,reglas\r\n");
        foreach (var row in rows)
            csv.Append(CultureInfo.InvariantCulture,
                $"{month},{CsvText(row.DisplayName)},{row.MonthlySalary},{row.AbsenceAdjustment},{row.MedicalLeaveAdjustment},{row.AdditionalDayAdjustment},{row.Total},{row.RulesVersion}\r\n");
        csv.Append(CultureInfo.InvariantCulture, $"{month},TOTAL,,,,,{rows.Sum(x => x.Total)},\r\n");
        return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"nomina-{month}.csv");
    }

    private static string CsvText(string value)
    {
        if (value.TrimStart() is ['=' or '+' or '-' or '@', ..]) value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static async Task<List<Receipt>> Calculate(AppDb db, DateOnly period, CancellationToken ct)
    {
        var people = await db.People.AsNoTracking().Where(x => x.Active && x.Salary > 0).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.Salary }).ToListAsync(ct);
        var attendance = (await ForPeriod(db, period).AsNoTracking().OrderBy(x => x.Date).ToListAsync(ct))
            .ToLookup(x => x.PersonId);
        return people.Select(p =>
        {
            var events = attendance[p.Id].Select(x => new WorkEvent(x.Date, x.Kind)).ToArray();
            var estimate = PayrollCalculator.Calculate(period, p.Salary, events);
            return new Receipt(period, p.Id, $"{p.FirstName} {p.LastName}", p.Salary, (long)estimate.AbsenceAdjustment,
                (long)estimate.MedicalLeaveAdjustment, (long)estimate.AdditionalDayAdjustment, (long)estimate.Total, estimate.RulesVersion,
                JsonSerializer.Serialize(events, JsonSerializerOptions.Web));
        }).ToList();
    }

    private static IQueryable<Attendance> ForPeriod(AppDb db, DateOnly period)
    {
        var last = period.AddDays(DateTime.DaysInMonth(period.Year, period.Month) - 1);
        return db.Events.Where(x => x.Date >= period && x.Date <= last);
    }

    private static Task<List<Guid>> BlockedPeople(AppDb db, DateOnly period, CancellationToken ct) =>
        (from entry in ForPeriod(db, period)
         join person in db.People on entry.PersonId equals person.Id
         where !person.Active || person.Salary <= 0
         select entry.PersonId).Distinct().OrderBy(id => id).ToListAsync(ct);

    private static object Summary(ClosedPeriod close) => new
    {
        close.Period,
        close.Revision,
        close.ClosedAt,
        close.ClosedBy,
        close.RulesVersion,
        peopleCount = close.Receipts.Count,
        total = close.Receipts.Sum(x => x.Total)
    };

    private static object ReceiptView(Receipt x) => new
    {
        x.Period,
        x.PersonId,
        x.DisplayName,
        x.MonthlySalary,
        x.AbsenceAdjustment,
        x.MedicalLeaveAdjustment,
        x.AdditionalDayAdjustment,
        x.Total,
        x.RulesVersion,
        events = JsonSerializer.Deserialize<WorkEvent[]>(x.Evidence, JsonSerializerOptions.Web)
    };

    private static bool TryDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryPeriod(string value, out DateOnly period) => TryDate($"{value}-01", out period);

    private sealed record EventInput(DateOnly? Date, WorkEventKind? Kind);
    private sealed record CloseInput(long? Revision);
    private enum EventOperation { Create, Update, Delete }
}
