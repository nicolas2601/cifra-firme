using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Reference.Api.Tests;

public sealed class ApiTests : IDisposable
{
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"reference-{Guid.NewGuid():N}.db");
    private readonly string _sql = Environment.GetEnvironmentVariable("TEST_SQLSERVER") ?? "";
    private WebApplicationFactory<Program>? _factory;

    [Fact]
    public async Task HealthAndAnonymousBoundaries()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/people")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/auth/register", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = _password })).StatusCode);
    }

    [Fact]
    public async Task ConcurrentDuplicatesAndClosureKeepOneImmutableExport()
    {
        using var admin = await Login();
        var person = await CreatePerson(admin, "ana", "worker", 1_800_000, "norte");
        var eventPath = $"/api/people/{person}/events";
        var duplicate = new { date = "2026-04-02", kind = "medicalLeave" };
        var writes = await Task.WhenAll(admin.PostAsJsonAsync(eventPath, duplicate), admin.PostAsJsonAsync(eventPath, duplicate));
        Assert.Equal(1, writes.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, writes.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var conflict = await admin.PostAsJsonAsync(eventPath, new { date = "2026-04-02", kind = "absence" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var preview = await Json(await admin.GetAsync("/api/payroll/2026-04/preview"));
        var revision = preview.GetProperty("revision").GetInt64();

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(eventPath,
            new { date = "2026-04-03", kind = "additionalDay" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/payroll/2026-04/close", new { revision })).StatusCode);
        preview = await Json(await admin.GetAsync("/api/payroll/2026-04/preview"));
        revision = preview.GetProperty("revision").GetInt64();

        var closures = await Task.WhenAll(admin.PostAsJsonAsync("/api/payroll/2026-04/close", new { revision }),
            admin.PostAsJsonAsync("/api/payroll/2026-04/close", new { revision }));
        Assert.Equal(1, closures.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, closures.Count(r => r.StatusCode == HttpStatusCode.OK));
        var receiptPath = $"/api/payroll/2026-04/people/{person}";
        var before = await admin.GetStringAsync(receiptPath);
        Assert.Equal(1_855_000, JsonDocument.Parse(before).RootElement.GetProperty("total").GetInt64());
        var csvBefore = await admin.GetStringAsync("/api/payroll/2026-04/export");
        Assert.Contains("1855000", csvBefore);

        var profile = Profile("ana", "worker", 2_700_000, "norte");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/people/{person}", profile)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{eventPath}/2026-04-02")).StatusCode);
        Assert.Equal(before, await admin.GetStringAsync(receiptPath));
        Assert.Equal(csvBefore, await admin.GetStringAsync("/api/payroll/2026-04/export"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/people/{person}")).StatusCode);
        Assert.Equal(before, await admin.GetStringAsync(receiptPath));
    }

    [Fact]
    public async Task RolesAreEnforcedPerActionAndPerson()
    {
        using var admin = await Login();
        var workerId = await CreatePerson(admin, "worker", "worker", 1_800_000, "norte");
        var otherId = await CreatePerson(admin, "other", "worker", 2_000_000, "sur");
        await CreatePerson(admin, "lead", "lead", 0, "norte");
        await CreatePerson(admin, "accountant", "accountant", 0, null);
        using var worker = await Login("worker");
        using var lead = await Login("lead");
        using var accountant = await Login("accountant");
        var entry = new { date = "2026-04-02", kind = "worked" };
        Assert.Equal(HttpStatusCode.Created, (await lead.PostAsJsonAsync($"/api/people/{workerId}/events", entry)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.PostAsJsonAsync($"/api/people/{otherId}/events", entry)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"/api/people/{workerId}/events", entry)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.PostAsJsonAsync("/api/payroll/2026-04/close", new { revision = 0 })).StatusCode);
        var preview = await Json(await admin.GetAsync("/api/payroll/2026-04/preview"));
        var revision = preview.GetProperty("revision").GetInt64();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/payroll/2026-04/close", new { revision })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync($"/api/payroll/2026-04/people/{workerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync($"/api/payroll/2026-04/people/{otherId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/payroll/2026-04/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync("/api/payroll/2026-04/export")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/people/{workerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await worker.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task CustomRoleAndKanbanAreIndependentFromPayroll()
    {
        using var admin = await Login();
        var role = new { key = "coord", name = "Coordinación", permissions = new[] { "activityManage" } };
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/roles", role)).StatusCode);
        var assignee = await CreatePerson(admin, "coord", "coord", 0, "norte");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/roles/coord")).StatusCode);
        using var coord = await Login("coord");
        var activity = new { title = "Revisar herramientas", assigneeId = assignee, status = "pending" };
        var item = await Json(await coord.PostAsJsonAsync("/api/activities", activity), HttpStatusCode.Created);
        var id = item.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await coord.PutAsJsonAsync($"/api/activities/{id}",
            new { activity.title, activity.assigneeId, status = "finished" })).StatusCode);
        var board = await Json(await coord.GetAsync("/api/activities"));
        Assert.Equal("finished", board[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await coord.GetAsync("/api/payroll/2026-04/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/roles/coord",
            new { role.name, permissions = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coord.GetAsync("/api/activities")).StatusCode);
    }

    [Fact]
    public async Task DeactivationWithReportedWorkBlocksTheCloseRatherThanDroppingThePerson()
    {
        using var admin = await Login();
        var id = await CreatePerson(admin, "ana", "worker", 1_800_000, "norte");
        var path = $"/api/people/{id}/events";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(path,
            new { date = "2026-04-02", kind = "worked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/people/{id}")).StatusCode);
        var preview = await Json(await admin.GetAsync("/api/payroll/2026-04/preview"));
        Assert.Equal(id, preview.GetProperty("blockedPeople")[0].GetGuid());
        var rejected = await admin.PostAsJsonAsync("/api/payroll/2026-04/close",
            new { revision = preview.GetProperty("revision").GetInt64() });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("sin salario o inactivas", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{path}/2026-04-02")).StatusCode);
        preview = await Json(await admin.GetAsync("/api/payroll/2026-04/preview"));
        Assert.Empty(preview.GetProperty("blockedPeople").EnumerateArray());
    }

    [Fact]
    public async Task CrewCanFindItsPeopleWithoutReadingTheirSalaries()
    {
        using var admin = await Login();
        var north = await CreatePerson(admin, "ana", "worker", 1_800_000, "norte");
        await CreatePerson(admin, "otra", "worker", 2_000_000, "sur");
        await CreatePerson(admin, "lead", "lead", 0, "norte");
        using var lead = await Login("lead");
        var crew = await Json(await lead.GetAsync("/api/crew/people"));
        var person = Assert.Single(crew.EnumerateArray());
        Assert.Equal(north, person.GetProperty("id").GetGuid());
        Assert.False(person.TryGetProperty("salary", out _));
        Assert.False(person.TryGetProperty("document", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await lead.GetAsync($"/api/people/{north}")).StatusCode);
    }

    [Fact]
    public async Task ProtectsUnsafeRequestsAndTheLastAdministrator()
    {
        using var admin = await Login();
        var me = await Json(await admin.GetAsync("/api/auth/me"));
        var path = $"/api/people/{me.GetProperty("id").GetGuid()}";
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync(path)).StatusCode);
        admin.DefaultRequestHeaders.Remove("X-CSRF-Token");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync(path)).StatusCode);
        await SetCsrf(admin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task CreatedResourcesResolveAndCsvNamesAreExportedAsText()
    {
        using var admin = await Login();
        var profile = new
        {
            username = "ana",
            firstName = "=2+3",
            lastName = "Demo",
            document = "DEMO-TEST",
            crew = "norte",
            role = "worker",
            salary = 1_800_000,
            active = true,
            password = _password,
            addresses = new[] { "Dirección ficticia" },
            phones = new[] { "3000000000" }
        };
        var created = await Json(await admin.PostAsJsonAsync("/api/people", profile), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var entry = await admin.PostAsJsonAsync($"/api/people/{id}/events", new { date = "2026-04-02", kind = "worked" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(entry.Headers.Location)).StatusCode);
        var item = await admin.PostAsJsonAsync("/api/activities", new { title = "Demo", assigneeId = id, status = "pending" });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(item.Headers.Location)).StatusCode);
        var preview = await Json(await admin.GetAsync("/api/payroll/2026-04/preview"));
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/payroll/2026-04/close",
            new { revision = preview.GetProperty("revision").GetInt64() })).StatusCode);
        Assert.Contains("\"'=2+3 Demo\"", await admin.GetStringAsync("/api/payroll/2026-04/export"));
    }

    private async Task<Guid> CreatePerson(HttpClient client, string username, string role, long salary, string? crew)
    {
        var profile = await Json(await client.PostAsJsonAsync("/api/people", Profile(username, role, salary, crew)),
            HttpStatusCode.Created);
        Assert.False(profile.TryGetProperty("password", out _));
        Assert.False(profile.TryGetProperty("passwordHash", out _));
        return profile.GetProperty("id").GetGuid();
    }

    private object Profile(string username, string role, long salary, string? crew) => new
    {
        username,
        role,
        salary,
        crew,
        password = _password,
        firstName = username,
        lastName = "Demo",
        document = $"DOC-{username}",
        addresses = new[] { "Dirección ficticia 1" },
        phones = new[] { "3000000000" },
        active = true
    };

    private async Task<HttpClient> Login(string username = "admin")
    {
        var client = Client();
        await SetCsrf(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { username, password = _password })).StatusCode);
        await SetCsrf(client);
        return client;
    }

    private static async Task SetCsrf(HttpClient client)
    {
        var body = await Json(await client.GetAsync("/api/auth/csrf"));
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", body.GetProperty("token").GetString());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private HttpClient Client()
    {
        _factory ??= new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", _sql.Length > 0 ? "SqlServer" : "Sqlite");
            builder.UseSetting("ConnectionStrings:Payroll", _sql.Length > 0
                ? new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_sql) { InitialCatalog = $"Reference_{Guid.NewGuid():N}" }.ConnectionString
                : $"Data Source={_dbPath};Default Timeout=10;Pooling=False");
            builder.UseSetting("Database:Initialize", "true");
            builder.UseSetting("Bootstrap:Username", "admin");
            builder.UseSetting("Bootstrap:Password", _password);
        });
        return _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _factory?.Dispose();
        File.Delete(_dbPath);
    }
}
