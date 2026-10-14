using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChargeGlobe.Domain.Charging;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.IntegrationTests;

/// <summary>/emps and /tariffs: EMPs and their tariffs, maintained with tariff:write.</summary>
[Collection(IdentityCollection.Name)]
public class TariffsTests(IntegrationFactory factory)
{
    /// <summary>One login for all tests of the class: logins count against the rate limit of /auth.</summary>
    private static string? _adminToken;

    private async Task<HttpClient> AdminAsync()
    {
        _adminToken ??= (await factory.LoginAsync(IntegrationFactory.AdminEmail, IntegrationFactory.AdminPassword)).AccessToken;
        return factory.Client(_adminToken);
    }

    private static string NewKey() => $"emp-{Guid.NewGuid():N}"[..20];

    private static object Tariff(string emp, string name = "Basic", object? operatorIds = null, bool roamingOnly = true) => new
    {
        emp,
        name,
        currency = "eur",
        validFrom = "2026-01-01T00:00:00Z",
        validTo = (string?)null,
        pricePerKwhAc = 0.45m,
        pricePerKwhDc = 0.65m,
        blockingFeePerMinute = 0.10m,
        blockingAfterMinutes = 120,
        monthlyFee = 4.90m,
        roamingOnly,
        operatorIds,
    };

    private Task<int> AuditCountAsync(string action, string target) =>
        factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.Action == action && entry.Target == target));

    [Fact]
    public async Task Reading_needs_price_read_and_writing_tariff_write()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/v1/emps")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/v1/tariffs")).StatusCode);

        var email = $"tariffs-{Guid.NewGuid():N}@chargeglobe.test";
        await factory.CreateConfirmedUserAsync(email);
        var user = factory.Client((await factory.LoginAsync(email)).AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/emps")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/tariffs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync($"/api/v1/emps/{NewKey()}", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync($"/api/v1/tariffs/{Guid.NewGuid()}", Tariff("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.DeleteAsync($"/api/v1/tariffs/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task An_emp_is_created_once_changed_by_put_and_deleted_without_tariffs()
    {
        var admin = await AdminAsync();
        var key = NewKey();

        var created = await admin.PutAsJsonAsync($"/api/v1/emps/{key}", new { name = " SMATRICS ", website = "https://smatrics.com" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/v1/emps/{key}", created.Headers.Location?.OriginalString);
        var data = (await created.ReadJsonAsync()).GetProperty("data");
        Assert.Equal((key, "SMATRICS", "https://smatrics.com", 0), (data.GetProperty("key").GetString(), data.GetProperty("name").GetString(), data.GetProperty("website").GetString(), data.GetProperty("tariffs").GetInt32()));

        // The same again changes nothing; a new name is recorded.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/emps/{key}", new { name = "SMATRICS", website = "https://smatrics.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/emps/{key}", new { name = "SMATRICS GmbH" })).StatusCode);
        Assert.Equal((1, 1), (await AuditCountAsync("emp.create", $"emp:{key}"), await AuditCountAsync("emp.update", $"emp:{key}")));

        var listed = (await (await admin.GetAsync("/api/v1/emps")).ReadJsonAsync()).GetProperty("data").EnumerateArray().Single(emp => emp.GetProperty("key").GetString() == key);
        Assert.Equal(("SMATRICS GmbH", JsonValueKind.Null), (listed.GetProperty("name").GetString(), listed.GetProperty("website").ValueKind));

        // Not while it has tariffs.
        var tariffId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await admin.PutAsJsonAsync($"/api/v1/tariffs/{tariffId}", Tariff(key))).StatusCode);
        await (await admin.DeleteAsync($"/api/v1/emps/{key}")).AssertProblemAsync(HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tariffs/{tariffId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/emps/{key}")).StatusCode);
        Assert.Equal(1, await AuditCountAsync("emp.delete", $"emp:{key}"));
        await (await admin.DeleteAsync($"/api/v1/emps/{key}")).AssertProblemAsync(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("X", """{"name":"X"}""", "key")]
    [InlineData("ok-key", """{"name":" "}""", "name")]
    [InlineData("ok-key", """{"name":"X","website":"ftp://example.com"}""", "website")]
    [InlineData("ok-key", """{"name":"X","website":"kein link"}""", "website")]
    public async Task Invalid_emps_are_rejected(string key, string body, string field)
    {
        var admin = await AdminAsync();

        var response = await admin.PutAsync($"/api/v1/emps/{key}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        var problem = await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_tariff_is_stored_with_the_id_of_the_client_and_put_is_idempotent()
    {
        var admin = await AdminAsync();
        var key = NewKey();
        await admin.PutAsJsonAsync($"/api/v1/emps/{key}", new { name = "ÖAMTC ePower" });
        var operatorId = await factory.WithDbAsync(async db =>
        {
            var @operator = Operator.Create("Tariff Operator", "AT");
            db.Operators.Add(@operator);
            await db.SaveChangesAsync();
            return @operator.Id;
        });
        var id = Guid.NewGuid();

        var created = await admin.PutAsJsonAsync($"/api/v1/tariffs/{id}", Tariff(key, operatorIds: new[] { operatorId, operatorId }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/v1/tariffs/{id}", created.Headers.Location?.OriginalString);
        var data = (await created.ReadJsonAsync()).GetProperty("data");
        Assert.Equal((id, key, "EUR"), (data.GetProperty("id").GetGuid(), data.GetProperty("emp").GetString(), data.GetProperty("currency").GetString()));
        Assert.Equal((0.45m, 0.65m, 4.90m, 120), (data.GetProperty("pricePerKwhAc").GetDecimal(), data.GetProperty("pricePerKwhDc").GetDecimal(), data.GetProperty("monthlyFee").GetDecimal(), data.GetProperty("blockingAfterMinutes").GetInt32()));
        Assert.Equal([operatorId], data.GetProperty("operatorIds").EnumerateArray().Select(item => item.GetGuid()));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("pricePerMinute").ValueKind);

        // Same values: 200 without a new audit entry; changed: recorded.
        var again = await admin.PutAsJsonAsync($"/api/v1/tariffs/{id}", Tariff(key, operatorIds: new[] { operatorId }));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/tariffs/{id}", Tariff(key, "Plus"))).StatusCode);
        Assert.Equal((1, 1), (await AuditCountAsync("tariff.create", $"tariff:{id}"), await AuditCountAsync("tariff.update", $"tariff:{id}")));

        var listed = (await (await admin.GetAsync($"/api/v1/tariffs?emp={key}")).ReadJsonAsync()).GetProperty("data");
        var tariff = Assert.Single(listed.EnumerateArray());
        Assert.Equal(("Plus", true), (tariff.GetProperty("name").GetString(), tariff.GetProperty("roamingOnly").GetBoolean()));
        Assert.Empty(tariff.GetProperty("operatorIds").EnumerateArray());
        Assert.Contains((await (await admin.GetAsync("/api/v1/tariffs")).ReadJsonAsync()).GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        Assert.Equal(1, (await (await admin.GetAsync("/api/v1/emps")).ReadJsonAsync()).GetProperty("data").EnumerateArray().Single(emp => emp.GetProperty("key").GetString() == key).GetProperty("tariffs").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tariffs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tariffs/{id}")).StatusCode);
        Assert.Equal(1, await AuditCountAsync("tariff.delete", $"tariff:{id}"));
        Assert.Empty((await (await admin.GetAsync($"/api/v1/tariffs?emp={key}")).ReadJsonAsync()).GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task A_tariff_needs_a_known_emp_and_known_operators()
    {
        var admin = await AdminAsync();
        var key = NewKey();

        var unknownEmp = await (await admin.PutAsJsonAsync($"/api/v1/tariffs/{Guid.NewGuid()}", Tariff(key))).AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(unknownEmp.GetProperty("errors").TryGetProperty("emp", out _));

        await admin.PutAsJsonAsync($"/api/v1/emps/{key}", new { name = "Known" });
        var unknownOperator = await (await admin.PutAsJsonAsync($"/api/v1/tariffs/{Guid.NewGuid()}", Tariff(key, operatorIds: new[] { Guid.NewGuid() })))
            .AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(unknownOperator.GetProperty("errors").TryGetProperty("operatorIds", out _));
    }

    public static TheoryData<string, string> InvalidTariffs() => new()
    {
        { """{"name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z"}""", "emp" },
        { """{"emp":"known","name":" ","currency":"EUR","validFrom":"2026-01-01T00:00:00Z"}""", "name" },
        { """{"emp":"known","name":"T","currency":"EURO","validFrom":"2026-01-01T00:00:00Z"}""", "currency" },
        { """{"emp":"known","name":"T","currency":"EUR"}""", "validFrom" },
        { """{"emp":"known","name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","validTo":"2026-01-01T00:00:00Z"}""", "validTo" },
        { """{"emp":"known","name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","pricePerKwhAc":-0.1}""", "pricePerKwhAc" },
        { """{"emp":"known","name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","pricePerKwhDc":1001}""", "pricePerKwhDc" },
        { """{"emp":"known","name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","pricePerMinute":-1}""", "pricePerMinute" },
        { """{"emp":"known","name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","sessionFee":-1}""", "sessionFee" },
        { """{"emp":"known","name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","blockingFeePerMinute":-1,"blockingAfterMinutes":10}""", "blockingFeePerMinute" },
        { """{"emp":"known","name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","monthlyFee":-1}""", "monthlyFee" },
        { """{"emp":"known","name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","blockingFeePerMinute":0.1,"blockingAfterMinutes":1441}""", "blockingAfterMinutes" },
        { """{"emp":"known","name":"T","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","blockingFeePerMinute":0.1}""", "blockingAfterMinutes" },
        { "{\"emp\":\"known\",\"name\":\"T\",\"currency\":\"EUR\",\"validFrom\":\"2026-01-01T00:00:00Z\",\"operatorIds\":[" + string.Join(',', Enumerable.Range(0, 101).Select(_ => $"\"{Guid.NewGuid()}\"")) + "]}", "operatorIds" },
    };

    [Theory]
    [MemberData(nameof(InvalidTariffs))]
    public async Task Invalid_tariffs_are_rejected(string body, string field)
    {
        var admin = await AdminAsync();

        var response = await admin.PutAsync($"/api/v1/tariffs/{Guid.NewGuid()}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        var problem = await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }
}
