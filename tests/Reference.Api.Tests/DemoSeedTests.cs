using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Reference.Api.Tests;

public sealed class DemoSeedTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(150)]
    public async Task SeedIsRepeatableAndLeavesAprilOpen(int workers)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cifra-{Guid.NewGuid():N}.db");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
        try
        {
            string? firstHistory = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development");
                    b.UseSetting("ConnectionStrings:Payroll", $"Data Source={file};Pooling=False");
                    b.UseSetting("Database:Initialize", "true");
                    b.UseSetting("Bootstrap:Username", "admin");
                    b.UseSetting("Bootstrap:Password", password);
                    b.UseSetting("Demo:Seed", "true");
                    b.UseSetting("Demo:Password", password);
                    b.UseSetting("Demo:Workers", workers.ToString(System.Globalization.CultureInfo.InvariantCulture));
                });
                using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
                var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
                client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf.GetProperty("token").GetString());
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password })).StatusCode);
                var preview = await client.GetFromJsonAsync<JsonElement>("/api/payroll/2026-04/preview");
                var receipts = preview.GetProperty("people");
                Assert.Equal(workers, receipts.GetArrayLength());
                var ana = Assert.Single(receipts.EnumerateArray(), x => x.GetProperty("displayName").GetString() == "Ana Rivera");
                Assert.Equal(1_695_000, ana.GetProperty("total").GetInt64());
                Assert.Empty(preview.GetProperty("blockedPeople").EnumerateArray());
                var history = await client.GetStringAsync("/api/payroll");
                Assert.Equal(2, JsonDocument.Parse(history).RootElement.GetArrayLength());
                Assert.Contains("2026-03-01", history);
                if (attempt == 0) firstHistory = history;
                else Assert.Equal(firstHistory, history);
                Assert.Equal(3, (await client.GetFromJsonAsync<JsonElement>("/api/activities")).EnumerateArray()
                    .Select(a => a.GetProperty("status").GetString()).Distinct().Count());
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SeedIsRejectedOutsideDevelopment()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Payroll", "Data Source=:memory:");
            b.UseSetting("Demo:Seed", "true");
        });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("solo está disponible en Development con SQLite", exception.Message);
    }
}
