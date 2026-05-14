using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RAG_API.Web;
using RAG_API.Web.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Load environment-specific appsettings
// For Blazor WASM, appsettings are loaded from wwwroot/appsettings.json
// During development: uses appsettings.Development.json (if exists)
// During production publish: appsettings.Production.json is copied to appsettings.json
var apiBase = builder.Configuration["ApiBaseUrl"]
    ?? builder.HostEnvironment.BaseAddress;

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped<RagApiClient>();

await builder.Build().RunAsync();
