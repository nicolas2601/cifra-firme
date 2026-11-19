using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Reference.Api;

internal sealed class DummyPassword
{
    public Person User { get; } = new();

    public DummyPassword()
    {
        User.PasswordHash = new PasswordHasher<Person>().HashPassword(User,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }
}

internal static class Access
{
    private const string ActorKey = "reference.actor";
    private const string SessionClaim = "session_version";

    public static bool Has(this Person actor, Permission required) => (actor.Role.Grants & required) == required;
    public static bool Any(this Person actor, Permission allowed) => (actor.Role.Grants & allowed) != Permission.None;
    public static Person Actor(HttpContext http) => (Person)http.Items[ActorKey]!;

    public static async Task<Person?> ReloadActor(AppDb db, Person actor, CancellationToken ct) =>
        await db.People.AsNoTracking().Include(x => x.Role)
            .SingleOrDefaultAsync(x => x.Id == actor.Id && x.Active && x.SessionVersion == actor.SessionVersion, ct);

    public static async Task ValidateSession(CookieValidatePrincipalContext ctx)
    {
        if (Guid.TryParse(ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            && Guid.TryParse(ctx.Principal.FindFirstValue(SessionClaim), out var version))
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            var person = await db.People.AsNoTracking().Include(x => x.Role)
                .SingleOrDefaultAsync(x => x.Id == id && x.Active && x.SessionVersion == version, ctx.HttpContext.RequestAborted);
            if (person is not null)
            {
                ctx.HttpContext.Items[ActorKey] = person;
                return;
            }
        }
        ctx.RejectPrincipal();
    }

    public static void MapAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapGet("/csrf", (HttpContext http, IAntiforgery csrf) => Results.Ok(new { token = csrf.GetAndStoreTokens(http).RequestToken }));
        auth.MapPost("/login", Login).RequireRateLimiting("login");
        auth.MapGet("/me", (HttpContext http) => Results.Ok(Session(Actor(http)))).RequireAuthorization();
        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task<IResult> Login(LoginInput input, AppDb db, IPasswordHasher<Person> hasher,
        DummyPassword dummy, HttpContext http, CancellationToken ct)
    {
        var username = CleanKey(input.Username, 48);
        if (username is null || input.Password is null || input.Password.Length > 128)
            return Results.Unauthorized();

        var person = await db.People.AsNoTracking().Include(x => x.Role)
            .SingleOrDefaultAsync(x => x.Username == username && x.Active, ct);
        var hashOwner = person ?? dummy.User;
        if (hasher.VerifyHashedPassword(hashOwner, hashOwner.PasswordHash, input.Password) == PasswordVerificationResult.Failed
            || person is null)
            return Results.Unauthorized();

        var identity = new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, person.Id.ToString()), new(SessionClaim, person.SessionVersion.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(new ClaimsPrincipal(identity));
        return Results.Ok(Session(person));
    }

    private static object Session(Person person) => new
    {
        person.Id,
        person.Username,
        person.FirstName,
        person.LastName,
        role = person.RoleKey,
        person.Crew,
        permissions = GrantNames(person.Role.Grants)
    };

    public static async Task Bootstrap(AppDb db, IPasswordHasher<Person> hasher, IConfiguration config)
    {
        var password = config["Bootstrap:Password"];
        if (password is null) return;
        await using var tx = await db.LockAsync(CancellationToken.None);
        if (await db.People.AnyAsync(x => x.RoleKey == "admin")) return;

        var username = CleanKey(config["Bootstrap:Username"], 48);
        if (username is null || !ValidPassword(password))
            throw new InvalidOperationException("Configura un nombre válido y una contraseña inicial de 12 a 128 caracteres.");
        var person = new Person
        {
            Username = username,
            FirstName = "Administrador",
            LastName = "Inicial",
            Document = "BOOTSTRAP",
            Contacts = """{"addresses":[],"phones":[]}""",
            RoleKey = "admin"
        };
        person.PasswordHash = hasher.HashPassword(person, password);
        db.People.Add(person);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public static string? CleanKey(string? input, int max)
    {
        var value = input?.Trim().ToLowerInvariant();
        return value is { Length: >= 2 } && value.Length <= max
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? value : null;
    }

    public static string? CleanText(string? input, int max)
    {
        var value = input?.Trim();
        return value is { Length: > 0 } && value.Length <= max && !value.Any(char.IsControl) ? value : null;
    }

    public static bool ValidPassword(string? value) => value is { Length: >= 12 and <= 128 } && !string.IsNullOrWhiteSpace(value);

    private static readonly Dictionary<string, Permission> Grants = Enum.GetValues<Permission>()
        .Where(x => x is not Permission.None and not Permission.All)
        .ToDictionary(x => char.ToLowerInvariant(x.ToString()[0]) + x.ToString()[1..]);

    public static string[] GrantNames(Permission permissions) =>
        Grants.Where(x => (permissions & x.Value) == x.Value).Select(x => x.Key).ToArray();

    public static Permission? ParseGrants(string[]? names)
    {
        if (names is null || names.Length > Grants.Count) return null;
        var result = Permission.None;
        foreach (var name in names)
        {
            if (name is null || !Grants.TryGetValue(name, out var item)) return null;
            result |= item;
        }
        return result;
    }

    private sealed record LoginInput(string? Username, string? Password);
}
