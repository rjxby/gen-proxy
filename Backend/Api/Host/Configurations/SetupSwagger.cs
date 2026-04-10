using Microsoft.OpenApi;

namespace GenProxy.Api.Host.Configurations;

public static class SetupSwagger
{
    public static IServiceCollection AddSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc(Constants.API.Version, new OpenApiInfo
            {
                Title = Constants.API.Title,
                Version = Constants.API.Version,
                Description = "Public Responses API backed by llama runtime token budgeting and generation."
            });

            c.DescribeAllParametersInCamelCase();
            c.OrderActionsBy(x => x.RelativePath);
        });

        return services;
    }

    public static IApplicationBuilder UseSwaggerSetup(this IApplicationBuilder app)
    {
        app.UseSwagger()
            .UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint($"/swagger/{Constants.API.Version}/swagger.json", Constants.API.Version);
            });
        return app;
    }
}
