// Admin UI: a guided wizard over the Admin API. All rules (validation, publishing, roles)
// are enforced by the Admin API; the UI only makes them easy to follow.

using ApiAggregator.Admin.UI.Components;
using ApiAggregator.Admin.UI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<AdminTokenProvider>();
builder.Services.AddHttpClient<AdminApiClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["AdminApi:BaseUrl"] ?? "http://localhost:5210/"));

// Live logs of the gateway and the Admin API (the Logs page). Addresses come from "Gateway:BaseUrl" and "AdminApi:BaseUrl".
builder.Services.AddHttpClient<LiveLogClient>();

// Per browser tab: toasts, and what this user and installation can do (fetched once).
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<UiContext>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
