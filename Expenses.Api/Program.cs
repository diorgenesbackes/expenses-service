using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Expenses.Api.Configuration;
using Expenses.Api.Errors;
using Expenses.Api.HealthChecks;
using Expenses.Application.Service;
using Expenses.Application.Identity;
using Expenses.Infrastructure.Identity;
using Expenses.Domain.Repository;
using Expenses.Infrastructure.Persistence;
using Expenses.Infrastructure.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => { options.IncludeScopes = true; options.UseUtcTimestamp = true; options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });

builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
    manager.FeatureProviders.Add(new UsersControllerFeatureProvider(builder.Environment.IsDevelopment())))
    .ConfigureApiBehaviorOptions(options =>
    {
        options.SuppressMapClientErrors = true;
        options.InvalidModelStateResponseFactory = context =>
        {
            var status = context.ModelState.Values.SelectMany(value => value.Errors)
                .Any(error => error.Exception is Microsoft.AspNetCore.Mvc.ModelBinding.UnsupportedContentTypeException) ? 415 : 400;
            var problem = ApiProblems.Create(context.HttpContext, status,
                status == 415 ? "UNSUPPORTED_MEDIA_TYPE" : "REQUEST_VALIDATION_FAILED");
            problem.Extensions["errors"] = new Dictionary<string, string[]> { ["$"] = ["Revise o formato e os campos da requisição."] };
            return new Microsoft.AspNetCore.Mvc.ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
        };
    });
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
builder.Services.AddIdentityProvider(builder.Configuration);
builder.Services.AddDataProtection().SetApplicationName("Expenses.Identity");
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = "cc_csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
// Only protected endpoints resolve the session scheme; health checks remain independent of Cognito.
AppContext.SetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", true);
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options => options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(SessionAuthenticationHandler.SchemeName)
    .RequireAuthenticatedUser().Build());
builder.Services.AddSingleton<SessionCookies>();
builder.Services.AddScoped<SessionCsrfFilter>();
builder.Services.AddSingleton<IEmailHasher>(_ => new EmailHasher(builder.Configuration["Identity:EmailHmacKey"] ?? ""));
builder.Services.AddSingleton<IAccessTokenValidator, CognitoAccessTokenValidator>();
builder.Services.AddScoped<ISessionStore, SessionStore>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddScoped<IRegistrationStore, RegistrationStore>();
builder.Services.AddScoped<IRegistrationService, RegistrationService>();
builder.Services.AddSingleton<IRegistrationObserver, RegistrationObserver>();

var app = builder.Build();

app.UseMiddleware<RequestContextMiddleware>();
app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
app.UseStatusCodePages(context => ApiProblems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode,
    context.HttpContext.Response.StatusCode switch { 404 => "RESOURCE_NOT_FOUND", 405 => "METHOD_NOT_ALLOWED", 415 => "UNSUPPORTED_MEDIA_TYPE", _ => "REQUEST_REJECTED" }));
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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program;
