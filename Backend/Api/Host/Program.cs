using GenProxy.Api.Host.Configurations;
using GenProxy.Api.Host.Endpoints;
using GenProxy.Api.Host.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecurity(builder.Configuration, builder.Environment);
builder.Services.AddIntegrationLayer(builder.Configuration, builder.Environment);
builder.Services.AddServiceLayer(builder.Configuration);
builder.Services.AddPresentationLayer(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwaggerSetup();
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseMiddleware<RequestSizeLimitMiddleware>();
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();
app.UseRateLimiter();

app.MapResponsesEndpoint();

await app.RunAsync();

public partial class Program { }
