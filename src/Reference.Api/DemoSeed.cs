using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Reference.Payroll;

namespace Reference.Api;

internal static class DemoSeed
{
    private const string Marker = "demo.seed.v1";
    private static readonly DateOnly ReviewMonth = new(2026, 4, 1);
    private static readonly string[] Crews = ["norte", "sur", "centro", "occidente"];
    private static readonly string[] Firsts = ["Valentina", "Santiago", "Camila", "Julián", "Isabel", "Nicolás", "Paula", "Felipe"];
    private static readonly string[] Lasts = ["Navarro", "Cárdenas", "López", "Martínez", "Méndez", "Duarte", "Pardo", "Mora"];

    public static async Task Load(AppDb db, IPasswordHasher<Person> hasher, IConfiguration config, CancellationToken ct)
    {
        await using var tx = await db.LockAsync(ct);
        if (await db.Audit.AnyAsync(x => x.Action == Marker, ct)) return;

        var people = await db.People.Take(2).ToListAsync(ct);
        if (people is not [{ RoleKey: "admin", Document: "BOOTSTRAP", Active: true } admin]
            || await db.Audit.AnyAsync(ct) || await db.Events.AnyAsync(ct) || await db.Closures.AnyAsync(ct)
            || await db.Activities.AnyAsync(ct) || await db.Roles.AnyAsync(x => !x.BuiltIn, ct))
            throw new InvalidOperationException("Usa una base demo nueva con solo la cuenta de administración inicial.");

        var password = config["Demo:Password"];
        var count = config.GetValue("Demo:Workers", 40);
        if (!Access.ValidPassword(password) || count is < 4 or > 150)
            throw new InvalidOperationException("Configura Demo__Password (12–128 caracteres) y Demo__Workers (4–150).");

        Person Create(string key, string first, string last, string role, string? crew = null, long salary = 0,
            bool active = true, bool signIn = false)
        {
            var person = new Person
            {
                Username = $"demo-{key}",
                Document = $"DEMO-{key.ToUpperInvariant()}",
                FirstName = first,
                LastName = last,
                RoleKey = role,
                Crew = crew,
                Salary = salary,
                Active = active,
                Contacts = JsonSerializer.Serialize(new
                {
                    addresses = new[] { $"Dirección ficticia · {crew ?? "oficina"} · {key}" },
                    phones = new[] { "0000000000" }
                }, JsonSerializerOptions.Web)
            };
            person.PasswordHash = hasher.HashPassword(person, signIn ? password! : Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
            db.People.Add(person);
            return person;
        }

        var ana = Create("ana", "Ana", "Rivera", "worker", "norte", 1_800_000, signIn: true);
        var mateo = Create("mateo", "Mateo", "Torres", "worker", "norte", 2_240_000);
        var lucia = Create("lucia", "Lucía", "Suárez", "worker", "sur", 1_950_000);
        var david = Create("david", "David", "Rojas", "worker", "sur", 2_120_000);
        var workers = new List<Person> { ana, mateo, lucia, david };
        for (var i = 0; i < count - 4; i++)
        {
            var first = i % Firsts.Length;
            var last = i / Firsts.Length;
            var surname = last < Lasts.Length ? Lasts[last] : $"{Lasts[last % Lasts.Length]} {Lasts[(last + 3) % Lasts.Length]}";
            workers.Add(Create($"persona-{i + 5:D3}", Firsts[first], surname, "worker", Crews[i % Crews.Length],
                1_650_000 + (i * 97_500 % 2_450_000)));
        }
        var lead = Create("jefa", "Luz", "Mendoza", "lead", "norte", signIn: true);
        Create("jefe-sur", "Samuel", "Vega", "lead", "sur");
        Create("conta", "Sol", "García", "accountant", signIn: true);
        var former = Create("retirada", "Elena", "Salcedo", "worker", "centro", 2_360_000, active: false);
        db.Roles.Add(new() { Key = "coordinacion", Name = "Coordinación de actividades", Grants = Permission.ActivityManage });
        var coordinator = Create("coord", "Andrea", "Reyes", "coordinacion");
        db.Activities.AddRange(
            new Activity { Title = "Revisar novedades de abril", AssigneeId = admin.Id, Status = TaskState.Pending },
            new Activity { Title = "Validar asignaciones de cuadrilla", AssigneeId = lead.Id, Status = TaskState.InProgress },
            new Activity { Title = "Actualizar datos de contacto", AssigneeId = ana.Id, Status = TaskState.Finished },
            new Activity { Title = "Organizar documentos del siguiente corte", AssigneeId = coordinator.Id, Status = TaskState.Pending });

        for (var offset = -2; offset <= 0; offset++)
        {
            var period = ReviewMonth.AddMonths(offset);
            var receipts = new List<Receipt>();
            var roster = offset == -2 ? [.. workers, former] : workers;
            for (var i = 0; i < roster.Count; i++)
            {
                var person = roster[i];
                WorkEvent[] events = offset == 0 && i == 0
                    ? [new(period.AddDays(1), WorkEventKind.Absence), new(period.AddDays(2), WorkEventKind.Absence),
                        new(period.AddDays(3), WorkEventKind.MedicalLeave), new(period.AddDays(4), WorkEventKind.MedicalLeave),
                        new(period.AddDays(5), WorkEventKind.MedicalLeave), new(period.AddDays(6), WorkEventKind.AdditionalDay)]
                    : i < 4 && offset == 0 ? []
                    : [new(period.AddDays(4 + i % 12), (WorkEventKind)(i % 4)),
                        new(period.AddDays(19 + i % 6), WorkEventKind.Worked)];
                db.Events.AddRange(events.Select(e => new Attendance { PersonId = person.Id, Date = e.Date, Kind = e.Kind }));
                if (offset == 0) continue;
                var estimate = PayrollCalculator.Calculate(period, person.Salary, events);
                receipts.Add(new(period, person.Id, $"{person.FirstName} {person.LastName}", person.Salary,
                    (long)estimate.AbsenceAdjustment, (long)estimate.MedicalLeaveAdjustment, (long)estimate.AdditionalDayAdjustment,
                    (long)estimate.Total, estimate.RulesVersion, JsonSerializer.Serialize(events, JsonSerializerOptions.Web)));
            }
            if (offset != 0)
            {
                var next = period.AddMonths(1);
                var closedAt = new DateTimeOffset(next.Year, next.Month, 1, 13, 0, 0, TimeSpan.Zero);
                db.Closures.Add(new()
                {
                    Period = period,
                    ClosedAt = closedAt,
                    ClosedBy = admin.Id,
                    Revision = offset + 3,
                    RulesVersion = receipts[0].RulesVersion,
                    Receipts = receipts
                });
                db.Audit.Add(new(Guid.NewGuid(), closedAt, admin.Id, "payroll.closed", period.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        await db.Clock.ExecuteUpdateAsync(x => x.SetProperty(c => c.Revision, c => c.Revision + 2), ct);
        await db.CommitAsync(tx, admin.Id, Marker, "2026-04", true, ct);
    }
}
