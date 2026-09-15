using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jarvis.Server.Tests;

public sealed class AgentTaskGatewayTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/artifacts")]
    public async Task Task_reads_require_authentication(string suffix)
    {
        using var app = new ServerFixture();
        using var client = app.Client();
        var response = await client.GetAsync($"/api/agent/tasks/{Guid.NewGuid()}{suffix}?deviceId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Creating_task_on_offline_owned_device_fails_explicitly()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        var enrolled = await (await admin.PostAsJsonAsync("/api/devices", new { name = "Task test device" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var response = await admin.PostAsJsonAsync("/api/agent/tasks", new
        {
            deviceId = enrolled.GetProperty("deviceId").GetString(),
            goal = "Verify a local build", executionMode = "NORMAL"
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Cookie_task_mutations_reject_missing_csrf()
    {
        using var app = new ServerFixture();
        using var admin = await app.Admin();
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var response = await admin.PostAsJsonAsync("/api/agent/tasks", new { deviceId = Guid.NewGuid(), goal = "test" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
