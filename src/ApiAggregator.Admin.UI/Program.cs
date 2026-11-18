// Admin UI: a guided wizard over the Admin API. All rules (validation, publishing, roles)
// are enforced by the Admin API; the UI only makes them easy to follow.

using ApiAggregator.Admin.UI.Components;
using ApiAggregator.Admin.UI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<AdminTokenProvider>();
builder.Services.AddHttpClient<AdminApiClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["AdminApi:BaseUrl"] ?? "http://localhost:5210/"));

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
