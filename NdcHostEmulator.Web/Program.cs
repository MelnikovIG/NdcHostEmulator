using MudBlazor.Services;
using NdcHostEmulator.Web.Components;
using NdcHostEmulator.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();
builder.Services.AddSignalR();

builder.Services.AddSingleton<TcpServerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TcpServerService>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
