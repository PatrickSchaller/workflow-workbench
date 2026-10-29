using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WorkflowWorkbench;
using Xunit;

namespace WorkflowWorkbench.Tests;

public class ApiTests
{
    [Fact]
    public async Task SessionIssuesASignedHttpOnlyCookieAndProtectedReadsRequireIt()
    {
        await using var app = new DemoApp();
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/versions")).StatusCode);
        var login = await client.PostAsJsonAsync("/api/session", new { actorId = "casey" });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        (await client.GetAsync("/api/versions")).EnsureSuccessStatusCode();
        using var forged = app.Client();
        forged.DefaultRequestHeaders.Add("Cookie", "WorkflowWorkbench.Demo=coordinator");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/versions")).StatusCode);
        await using var restarted = new DemoApp();
        using var oldSession = restarted.Client();
        oldSession.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/versions")).StatusCode);
    }

    [Fact]
    public async Task CompleteRequestThroughTheRealCookieAndRolePolicies()
    {
        await using var app = new DemoApp();
        using var client = app.Client();
        await SignIn(client, "alex");
        var run = await Create(client);
        run = await Complete(client, run, "intake");
        Assert.Equal("active", run.Steps["operations"]);
        Assert.Equal("active", run.Steps["finance"]);
        await SignIn(client, "morgan");
        run = await Complete(client, run, "operations");
        Assert.Equal("waiting", run.Steps["release"]);
        await SignIn(client, "sam");
        run = await Complete(client, run, "finance");
        Assert.Equal("active", run.Steps["release"]);
        await SignIn(client, "casey");
        run = await Complete(client, run, "release");
        Assert.Equal("complete", run.Status);
        Assert.Equal(5, run.History.Length);
        Assert.Equal("Casey — Coordinator", run.History[^1].Actor);
    }

    [Fact]
    public async Task NonCoordinatorCannotPublishOrSmuggleARoleInTheBody()
    {
        await using var app = new DemoApp();
        using var client = app.Client();
        await SignIn(client, "sam");
        var response = await client.PostAsJsonAsync("/api/versions", new PublishCommand(1, Engine.Example()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/session", new { actorId = "casey", role = "coordinator" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/versions", new PublishCommand(1, Engine.Example()))).StatusCode);
    }

    [Fact]
    public async Task WrongRoleQuestionAndStaleActionReturnUsefulFailuresWithoutChangingHistory()
    {
        await using var app = new DemoApp();
        using var client = app.Client();
        await SignIn(client, "alex");
        var run = await Create(client);
        var unanswered = await client.PostAsJsonAsync($"/api/runs/{run.Id}/actions", Command(run, "intake") with { Confirmed = false });
        Assert.Equal(HttpStatusCode.BadRequest, unanswered.StatusCode);
        await SignIn(client, "sam");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/runs/{run.Id}/actions", Command(run, "intake"))).StatusCode);
        await SignIn(client, "alex");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/runs/{run.Id}/actions", Command(run, "intake") with { ExpectedRevision = 99 })).StatusCode);
        var unchanged = await client.GetFromJsonAsync<RunView>($"/api/runs/{run.Id}");
        Assert.Single(unchanged!.History);
        Assert.Equal(0, unchanged.Revision);
    }

    [Fact]
    public async Task InvalidGraphAndOldPublishTokenLeavePublishedVersionUntouched()
    {
        await using var app = new DemoApp();
        using var client = app.Client();
        await SignIn(client, "casey");
        var definition = Engine.Example();
        definition.Steps[1] = definition.Steps[1] with { Requires = ["release"] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/versions", new PublishCommand(1, definition))).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<PublishedVersion[]>("/api/versions"))!);
        (await client.PostAsJsonAsync("/api/versions", new PublishCommand(1, Engine.Example()))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/versions", new PublishCommand(1, Engine.Example()))).StatusCode);
    }

    [Fact]
    public async Task HostAndCrossSiteFormProtectionsApplyBeforeTheSessionEndpoint()
    {
        await using var app = new DemoApp();
        using var client = app.Client();
        client.DefaultRequestHeaders.Remove("X-Workbench");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/session", new { actorId = "casey" })).StatusCode);
        using var hostile = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        hostile.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(hostile)).StatusCode);
    }

    private static async Task SignIn(HttpClient client, string actor) =>
        (await client.PostAsJsonAsync("/api/session", new { actorId = actor })).EnsureSuccessStatusCode();
    private static async Task<RunView> Create(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/runs", new CreateCommand("Fictional printer"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RunView>())!;
    }
    private static ActionCommand Command(RunView run, string step) => new(run.Revision, Guid.NewGuid().ToString("D"), step, "complete", "", true);
    private static async Task<RunView> Complete(HttpClient client, RunView run, string step)
    {
        var response = await client.PostAsJsonAsync($"/api/runs/{run.Id}/actions", Command(run, step));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ActionResult>())!.Run;
    }

    private sealed class DemoApp : WebApplicationFactory<Program>
    {
        private readonly string path = Path.Combine(AppContext.BaseDirectory, $"api-workbench-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Workbench:DatabasePath"] = path }));
            builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, Loopback>());
        }
        public HttpClient Client()
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://127.0.0.1") });
            client.DefaultRequestHeaders.Add("X-Workbench", "1");
            return client;
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return nextMiddleware(); });
            next(app);
        };
    }
}
