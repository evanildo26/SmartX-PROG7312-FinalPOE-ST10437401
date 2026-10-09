using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartX.Client;
using SmartX.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// API address comes from wwwroot/appsettings.json ("ApiBaseUrl") so different machines
// can point at different hosts without recompiling.
var apiBase = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7290";
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });

// In Blazor WebAssembly a scoped service lives for the whole browser session, so these
// hold application state that must survive navigation between the three pillars.
builder.Services.AddScoped<StreamStateService>();
builder.Services.AddScoped<IngestionState>();

await builder.Build().RunAsync();
