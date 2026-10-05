using Expenses.Api.Configuration;
using Expenses.Api.Errors;
using Expenses.Api.HealthChecks;
using Expenses.Application.Service;
using Expenses.Domain.Repository;
using Expenses.Infrastructure.Persistence;
using Expenses.Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
    manager.FeatureProviders.Add(new UsersControllerFeatureProvider(builder.Environment.IsDevelopment())));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name,
        timeout: TimeSpan.FromSeconds(DatabaseHealthCheck.TimeoutSeconds));
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Expenses API",
        Version = "v1",
        Description = "Documentação dos endpoints do Expenses. A listagem de usuários é um diagnóstico disponível em Development."
    });
    options.DescribeAllParametersInCamelCase();
    options.SupportNonNullableReferenceTypes();
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "Expenses.Api.xml"));
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "Expenses.Domain.xml"));
});
builder.Services.AddDbContext<ExpensesDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Expenses")
        ?? throw new InvalidOperationException("ConnectionStrings:Expenses is not configured.");
    options.UseNpgsql(connectionString, postgres => postgres.CommandTimeout(5));
});
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserService, UserService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpsRedirection();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("v1/swagger.json", "Expenses API v1");
        options.DocumentTitle = "Expenses API — Swagger";
    });
}
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program;
