using System.Net;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using WorkflowWorkbench;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.UseUrls("http://127.0.0.1:5099");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.MaxDepth = 16;
});
builder.Services.AddDataProtection().AddKeyManagementOptions(options => options.XmlRepository = new MemoryKeyRepository());
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "WorkflowWorkbench.Demo";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(4);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(options => options.AddPolicy("designer", policy => policy.RequireRole("coordinator")));
builder.Services.AddSingleton(provider => new WorkbenchStore(
    builder.Configuration["Workbench:DatabasePath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "workbench.db")));
var app = builder.Build();
// Initialize and validate the database before accepting traffic.
_ = app.Services.GetRequiredService<WorkbenchStore>();

app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
        || context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
    { context.Response.StatusCode = 403; return; }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    if (context.Request.Method == "POST" && context.Request.Headers["X-Workbench"] != "1")
    { context.Response.StatusCode = 403; return; }
    try { await next(context); }
    catch (WorkbenchException error)
    { await Results.Problem(error.Message, statusCode: error.Status).ExecuteAsync(context); }
    catch (BadHttpRequestException)
    { await Results.Problem("Request body is missing, malformed or too large.", statusCode: 400).ExecuteAsync(context); }
    catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
    { await Results.Problem("The database is busy. Reload and try again.", statusCode: 503).ExecuteAsync(context); }
    catch (Exception error)
    {
        app.Logger.LogError(error, "Workflow operation failed");
        await Results.Problem("The operation could not be committed. Reload the request before retrying.", statusCode: 500).ExecuteAsync(context);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/session", (ClaimsPrincipal user) => new
{ actors = Engine.Actors, actor = Engine.Actors.SingleOrDefault(actor => actor.Id == user.FindFirstValue(ClaimTypes.NameIdentifier)) });
app.MapPost("/api/session", async (SessionCommand request, HttpContext context) =>
{
    var actor = Engine.Actors.SingleOrDefault(actor => actor.Id == request.ActorId)
        ?? throw new WorkbenchException(400, "Choose one of the fictional demo identities.");
    var identity = new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, actor.Id), new Claim(ClaimTypes.Name, actor.Name), new Claim(ClaimTypes.Role, actor.Role)
    ], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(actor);
});
app.MapGet("/api/versions", (WorkbenchStore store) => store.Versions()).RequireAuthorization();
app.MapPost("/api/versions", (PublishCommand request, WorkbenchStore store, ClaimsPrincipal user) => store.Publish(request, Actor(user))).RequireAuthorization("designer");
app.MapGet("/api/runs", (WorkbenchStore store) => store.Runs()).RequireAuthorization();
app.MapPost("/api/runs", (CreateCommand request, WorkbenchStore store, ClaimsPrincipal user) => store.Create(request.Title, Actor(user))).RequireAuthorization();
app.MapGet("/api/runs/{id}", (string id, WorkbenchStore store) => store.Get(id)).RequireAuthorization();
app.MapPost("/api/runs/{id}/actions", (string id, ActionCommand request, WorkbenchStore store, ClaimsPrincipal user) => store.Apply(id, request, Actor(user))).RequireAuthorization();
app.Run();

static DemoActor Actor(ClaimsPrincipal user) => Engine.Actors.Single(actor => actor.Id == user.FindFirstValue(ClaimTypes.NameIdentifier));
public partial class Program;
