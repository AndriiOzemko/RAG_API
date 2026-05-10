using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using RAG_API.Web;
using RAG_API.Web.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ApiBaseUrl is read from wwwroot/appsettings.json by the browser
var apiBase = builder.Configuration["ApiBaseUrl"]
    ?? builder.HostEnvironment.BaseAddress;

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped<RagApiClient>();

await builder.Build().RunAsync();
