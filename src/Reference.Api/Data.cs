using Reference.Payroll;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Reference.Api;

[Flags]
internal enum Permission
{
    None = 0,
    PeopleManage = 1,
    RolesManage = 2,
    EventsAll = 4,
    EventsCrew = 8,
    PayrollClose = 16,
    PayrollReadAll = 32,
    PayrollReadOwn = 64,
    PayrollExport = 128,
    ActivityManage = 256,
    ActivityReadOwn = 512,
    All = 1023
}

internal sealed class AccessRole
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public Permission Grants { get; set; }
    public bool BuiltIn { get; set; }
}

internal sealed class Person
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Document { get; set; } = "";
    public string Contacts { get; set; } = "";
    public string? Crew { get; set; }
    public string RoleKey { get; set; } = "";
    public AccessRole Role { get; set; } = null!;
    public long Salary { get; set; }
    public bool Active { get; set; } = true;
    public string PasswordHash { get; set; } = "";
    public Guid SessionVersion { get; set; } = Guid.NewGuid();
}

internal sealed class Attendance
{
    public Guid PersonId { get; set; }
    public DateOnly Date { get; set; }
    public WorkEventKind Kind { get; set; }
}

internal sealed class ClosedPeriod
{
    public DateOnly Period { get; set; }
    public long Revision { get; set; }
    public string RulesVersion { get; set; } = "";
    public DateTimeOffset ClosedAt { get; set; }
    public Guid ClosedBy { get; set; }
    public List<Receipt> Receipts { get; set; } = [];
}

internal sealed record Receipt(
    DateOnly Period, Guid PersonId, string DisplayName, long MonthlySalary, long AbsenceAdjustment,
    long MedicalLeaveAdjustment, long AdditionalDayAdjustment, long Total, string RulesVersion, string Evidence);

internal sealed class ChangeClock
{
    public int Id { get; set; }
    public long Revision { get; set; }
    public bool Gate { get; set; }
}

internal enum TaskState { Pending, InProgress, Finished }

internal sealed class Activity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public Guid AssigneeId { get; set; }
    public TaskState Status { get; set; }
}

internal sealed record AuditEntry(Guid Id, DateTimeOffset At, Guid ActorId, string Action, string Target);

internal sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<AccessRole> Roles => Set<AccessRole>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Attendance> Events => Set<Attendance>();
    public DbSet<ClosedPeriod> Closures => Set<ClosedPeriod>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<ChangeClock> Clock => Set<ChangeClock>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var role = model.Entity<AccessRole>();
        role.HasKey(x => x.Key);
        role.Property(x => x.Key).HasMaxLength(32);
        role.Property(x => x.Name).HasMaxLength(80);
        role.HasData(
            new AccessRole { Key = "admin", Name = "Administración", Grants = Permission.All, BuiltIn = true },
            new AccessRole { Key = "lead", Name = "Jefatura", Grants = Permission.EventsCrew | Permission.ActivityReadOwn, BuiltIn = true },
            new AccessRole { Key = "worker", Name = "Trabajador", Grants = Permission.PayrollReadOwn | Permission.ActivityReadOwn, BuiltIn = true },
            new AccessRole { Key = "accountant", Name = "Contabilidad", Grants = Permission.PayrollReadAll | Permission.PayrollExport, BuiltIn = true });

        var person = model.Entity<Person>();
        person.Property(x => x.Username).HasMaxLength(48);
        person.Property(x => x.FirstName).HasMaxLength(80);
        person.Property(x => x.LastName).HasMaxLength(80);
        person.Property(x => x.Document).HasMaxLength(48);
        person.Property(x => x.Crew).HasMaxLength(32);
        person.Property(x => x.RoleKey).HasMaxLength(32);
        person.HasIndex(x => x.Username).IsUnique();
        person.HasIndex(x => x.Document).IsUnique();
        person.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleKey).OnDelete(DeleteBehavior.Restrict);

        var entry = model.Entity<Attendance>();
        entry.HasKey(x => new { x.PersonId, x.Date });
        entry.HasIndex(x => x.Date);
        entry.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);

        var close = model.Entity<ClosedPeriod>();
        close.HasKey(x => x.Period);
        close.Property(x => x.RulesVersion).HasMaxLength(32);
        close.HasOne<Person>().WithMany().HasForeignKey(x => x.ClosedBy).OnDelete(DeleteBehavior.Restrict);
        var receipt = model.Entity<Receipt>();
        receipt.HasKey(x => new { x.Period, x.PersonId });
        receipt.Property(x => x.DisplayName).HasMaxLength(161);
        receipt.Property(x => x.RulesVersion).HasMaxLength(32);
        receipt.HasOne<ClosedPeriod>().WithMany(x => x.Receipts).HasForeignKey(x => x.Period).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);

        model.Entity<ChangeClock>().HasData(new ChangeClock { Id = 1 });
        model.Entity<Activity>().Property(x => x.Title).HasMaxLength(160);
        model.Entity<Activity>().HasOne<Person>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AuditEntry>().HasOne<Person>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AuditEntry>().Property(x => x.Action).HasMaxLength(40);
        model.Entity<AuditEntry>().Property(x => x.Target).HasMaxLength(80);
    }

    public async Task<IDbContextTransaction> LockAsync(CancellationToken ct)
    {
        var transaction = await Database.BeginTransactionAsync(ct);
        try
        {
            // All writers take this lock first, even across app instances. The revision only changes on payroll inputs.
            var affected = await Clock.Where(x => x.Id == 1)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Gate, x => !x.Gate), ct);
            if (affected != 1)
                throw new InvalidOperationException("El esquema de nómina no está inicializado.");
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    public Task<long> RevisionAsync(CancellationToken ct) => Clock.Select(x => x.Revision).SingleAsync(ct);

    public async Task CommitAsync(IDbContextTransaction transaction, Guid actor, string action, string target,
        bool payrollChanged, CancellationToken ct)
    {
        if (payrollChanged)
            await Clock.ExecuteUpdateAsync(set => set.SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        Audit.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow, actor, action, target));
        await SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
