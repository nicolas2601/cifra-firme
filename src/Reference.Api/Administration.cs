using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static Reference.Api.Access;

namespace Reference.Api;

internal static class Administration
{
    public static void MapAdministration(this WebApplication app)
    {
        var people = app.MapGroup("/api/people").RequireAuthorization();
        people.MapGet("", ListPeople);
        people.MapGet("/{id:guid}", GetPerson);
        people.MapPost("", (ProfileInput input, AppDb db, IPasswordHasher<Person> hasher, HttpContext http, CancellationToken ct)
            => SavePerson(null, input, db, hasher, http, ct));
        people.MapPut("/{id:guid}", (Guid id, ProfileInput input, AppDb db, IPasswordHasher<Person> hasher, HttpContext http, CancellationToken ct)
            => SavePerson(id, input, db, hasher, http, ct));
        people.MapDelete("/{id:guid}", Deactivate);

        var roles = app.MapGroup("/api/roles").RequireAuthorization();
        roles.MapGet("", async (AppDb db, HttpContext http, CancellationToken ct) =>
        {
            if (!Actor(http).Any(Permission.RolesManage | Permission.PeopleManage)) return Results.Forbid();
            var rows = await db.Roles.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct);
            return Results.Ok(rows.Select(RoleView));
        });
        roles.MapGet("/{key}", async (string key, AppDb db, HttpContext http, CancellationToken ct) =>
        {
            if (!Actor(http).Any(Permission.RolesManage | Permission.PeopleManage)) return Results.Forbid();
            var role = await db.Roles.FindAsync([key], ct);
            return role is null ? Results.NotFound() : Results.Ok(RoleView(role));
        });
        roles.MapPost("", (RoleInput input, AppDb db, HttpContext http, CancellationToken ct)
            => SaveRole(null, input, db, http, ct));
        roles.MapPut("/{key}", (string key, RoleInput input, AppDb db, HttpContext http, CancellationToken ct)
            => SaveRole(key, input, db, http, ct));
        roles.MapDelete("/{key}", DeleteRole);
    }

    private static async Task<IResult> ListPeople(AppDb db, HttpContext http, CancellationToken ct, int page = 1, int size = 50)
    {
        if (!Actor(http).Has(Permission.PeopleManage)) return Results.Forbid();
        if (page is < 1 or > 100_000 || size is < 1 or > 150) return Bad("Paginación fuera de rango.");
        var rows = await db.People.AsNoTracking().OrderBy(x => x.LastName).ThenBy(x => x.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(rows.Select(PersonView));
    }

    private static async Task<IResult> GetPerson(Guid id, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.PeopleManage)) return Results.Forbid();
        var person = await db.People.FindAsync([id], ct);
        return person is null ? Results.NotFound() : Results.Ok(PersonView(person));
    }

    private static async Task<IResult> SavePerson(Guid? id, ProfileInput input, AppDb db, IPasswordHasher<Person> hasher,
        HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.PeopleManage)) return Results.Forbid();
        var username = CleanKey(input.Username, 48);
        var first = CleanText(input.FirstName, 80);
        var last = CleanText(input.LastName, 80);
        var document = CleanKey(input.Document, 48)?.ToUpperInvariant();
        var crew = CleanKey(input.Crew, 32);
        var roleKey = CleanKey(input.Role, 32);
        var addresses = CleanList(input.Addresses, 200);
        var phones = CleanList(input.Phones, 32);
        if (username is null || first is null || last is null || document is null || roleKey is null
            || addresses is null || phones is null || (input.Crew is not null && crew is null) || input.Active is null
            || input.Salary is null or < 0 or > 999_999_999_999L
            || (id is null || input.Password is not null) && !ValidPassword(input.Password))
            return Bad("Revisa los datos de la persona, contactos, estado, salario o contraseña.");

        await using var tx = await db.LockAsync(ct);
        var actor = await ReloadActor(db, Actor(http), ct);
        if (actor is null || !actor.Has(Permission.PeopleManage)) return Results.Forbid();
        var role = await db.Roles.FindAsync([roleKey], ct);
        if (role is null) return Bad("El rol no existe.");
        if (!actor.Has(role.Grants) || (roleKey == "admin" && actor.RoleKey != "admin")) return Results.Forbid();
        if (role.HasCrewRestriction() && crew is null) return Bad("El rol requiere una cuadrilla.");
        if (input.Salary == 0 && (role.Grants & Permission.PayrollReadOwn) != 0 && roleKey != "admin")
            return Bad("El salario de una persona liquidable debe ser mayor que cero.");

        var person = id is null ? new Person() : await db.People.Include(x => x.Role).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (person is null) return Results.NotFound();
        if (id is not null && !CanManage(actor, person)) return Results.Forbid();
        if (person.Active && person.RoleKey == "admin" && (!input.Active.Value || roleKey != "admin")
            && !await HasAnotherAdmin(db, person.Id, ct)) return Conflict("No se puede retirar al último administrador activo.");
        if (await db.People.AnyAsync(x => x.Id != person.Id && (x.Username == username || x.Document == document), ct))
            return Conflict("El usuario o documento ya existe.");

        var changed = id is null || person.Salary != input.Salary || person.Active != input.Active
            || person.FirstName != first || person.LastName != last;
        if (person.Active != input.Active || input.Password is not null) person.SessionVersion = Guid.NewGuid();
        if (input.Password is not null) person.PasswordHash = hasher.HashPassword(person, input.Password);
        person.Username = username;
        person.FirstName = first;
        person.LastName = last;
        person.Document = document;
        person.RoleKey = roleKey;
        person.Crew = crew;
        person.Salary = input.Salary.Value;
        person.Contacts = JsonSerializer.Serialize(new Contacts(addresses, phones), JsonSerializerOptions.Web);
        person.Active = input.Active.Value;
        if (id is null) db.People.Add(person);

        await db.CommitAsync(tx, actor.Id, id is null ? "person.created" : "person.changed", person.Id.ToString(), changed, ct);
        return id is null ? Results.Created($"/api/people/{person.Id}", PersonView(person)) : Results.Ok(PersonView(person));
    }

    private static async Task<IResult> Deactivate(Guid id, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.PeopleManage)) return Results.Forbid();
        await using var tx = await db.LockAsync(ct);
        var actor = await ReloadActor(db, Actor(http), ct);
        if (actor is null || !actor.Has(Permission.PeopleManage)) return Results.Forbid();
        var person = await db.People.Include(x => x.Role).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (person is null) return Results.NotFound();
        if (!CanManage(actor, person)) return Results.Forbid();
        if (!person.Active) return Results.NoContent();
        if (person.RoleKey == "admin" && !await HasAnotherAdmin(db, id, ct)) return Conflict("No se puede retirar al último administrador activo.");
        person.Active = false;
        person.SessionVersion = Guid.NewGuid();
        await db.CommitAsync(tx, actor.Id, "person.deactivated", id.ToString(), true, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveRole(string? id, RoleInput input, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.RolesManage)) return Results.Forbid();
        var key = CleanKey(id ?? input.Key, 32);
        var name = CleanText(input.Name, 80);
        var grants = ParseGrants(input.Permissions);
        if (key is null || name is null || grants is null) return Bad("Nombre, clave o permisos inválidos.");
        await using var tx = await db.LockAsync(ct);
        var actor = await ReloadActor(db, Actor(http), ct);
        if (actor is null || !actor.Has(Permission.RolesManage | grants.Value)) return Results.Forbid();
        var role = await db.Roles.FindAsync([key], ct);
        if (id is null && role is not null) return Conflict("La clave de rol ya existe.");
        if (id is not null && role is null) return Results.NotFound();
        if (role?.BuiltIn == true) return Conflict("Los roles base son fijos. Crea un rol personalizado.");
        if (role is not null && !actor.Has(role.Grants)) return Results.Forbid();
        if (role is not null && (grants & Permission.EventsCrew) != 0
            && await db.People.AnyAsync(x => x.RoleKey == key && x.Crew == null, ct))
            return Conflict("El rol está asignado a personas sin cuadrilla.");
        if (role is not null && (grants & Permission.PayrollReadOwn) != 0
            && await db.People.AnyAsync(x => x.RoleKey == key && x.Salary == 0, ct))
            return Conflict("El rol está asignado a personas sin salario liquidable.");
        role ??= new AccessRole { Key = key };
        role.Name = name;
        role.Grants = grants.Value;
        if (id is null) db.Roles.Add(role);
        await db.CommitAsync(tx, actor.Id, id is null ? "role.created" : "role.changed", key, false, ct);
        return id is null ? Results.Created($"/api/roles/{key}", RoleView(role)) : Results.Ok(RoleView(role));
    }

    private static async Task<IResult> DeleteRole(string key, AppDb db, HttpContext http, CancellationToken ct)
    {
        if (!Actor(http).Has(Permission.RolesManage)) return Results.Forbid();
        await using var tx = await db.LockAsync(ct);
        var actor = await ReloadActor(db, Actor(http), ct);
        if (actor is null || !actor.Has(Permission.RolesManage)) return Results.Forbid();
        var role = await db.Roles.FindAsync([key], ct);
        if (role is null) return Results.NotFound();
        if (role.BuiltIn) return Conflict("Los roles base no se eliminan.");
        if (!actor.Has(role.Grants)) return Results.Forbid();
        if (await db.People.AnyAsync(x => x.RoleKey == key, ct)) return Conflict("El rol está asignado.");
        db.Roles.Remove(role);
        await db.CommitAsync(tx, actor.Id, "role.deleted", key, false, ct);
        return Results.NoContent();
    }

    private static bool CanManage(Person actor, Person target) =>
        actor.Has(target.Role.Grants) && (target.RoleKey != "admin" || actor.RoleKey == "admin");

    private static bool HasCrewRestriction(this AccessRole role) =>
        (role.Grants & Permission.EventsCrew) != 0 && (role.Grants & Permission.EventsAll) == 0;

    private static Task<bool> HasAnotherAdmin(AppDb db, Guid id, CancellationToken ct) =>
        db.People.AnyAsync(x => x.Id != id && x.Active && x.RoleKey == "admin", ct);

    private static string[]? CleanList(string[]? input, int maxLength)
    {
        if (input is not { Length: >= 1 and <= 8 }) return null;
        var cleaned = new List<string>(input.Length);
        foreach (var item in input)
        {
            var value = CleanText(item, maxLength);
            if (value is null) return null;
            cleaned.Add(value);
        }
        return cleaned.ToArray();
    }

    private static object RoleView(AccessRole role) =>
        new { role.Key, role.Name, role.BuiltIn, permissions = GrantNames(role.Grants) };

    private static object PersonView(Person person)
    {
        var contact = JsonSerializer.Deserialize<Contacts>(person.Contacts, JsonSerializerOptions.Web)!;
        return new
        {
            person.Id,
            person.Username,
            person.FirstName,
            person.LastName,
            person.Document,
            person.Crew,
            role = person.RoleKey,
            person.Salary,
            person.Active,
            contact.Addresses,
            contact.Phones
        };
    }

    internal static IResult Bad(string detail) => Results.Problem(detail, statusCode: 400);
    internal static IResult Conflict(string detail) => Results.Problem(detail, statusCode: 409);

    private sealed record Contacts(string[] Addresses, string[] Phones);
    private sealed record ProfileInput(string? Username, string? FirstName, string? LastName, string? Document, string? Crew,
        string? Role, long? Salary, bool? Active, string? Password, string[]? Addresses, string[]? Phones);
    private sealed record RoleInput(string? Key, string? Name, string[]? Permissions);
}
