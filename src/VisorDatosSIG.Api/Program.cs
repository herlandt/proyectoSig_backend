using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using VisorDatosSIG.Api.Infraestructura;
using VisorDatosSIG.Application.Consultas;
using VisorDatosSIG.Domain;
using VisorDatosSIG.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Datos y casos de uso. La cadena de conexión viene de user-secrets (desarrollo) o de la
// variable de entorno ConnectionStrings__VisorDatosSIG (producción); nunca del código.
// Las fotos de evidencia de la app móvil se guardan fuera de wwwroot (Evidencias:Carpeta o %LOCALAPPDATA%).
builder.Services.AgregarVisorDatosSig(config.GetConnectionString("VisorDatosSIG") ?? "", config["Evidencias:Carpeta"]);

// Claves de protección compartidas con FrontendSIG: así ambos validan la misma cookie de sesión.
var rutaClaves = config["Seguridad:RutaClaves"];
if (string.IsNullOrWhiteSpace(rutaClaves))
    rutaClaves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisorDatosSIG", "claves");
var dpBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("VisorDatosSIG")
    .PersistKeysToFileSystem(new DirectoryInfo(rutaClaves));
if (OperatingSystem.IsWindows())
    dpBuilder.ProtectKeysWithDpapi(protectToLocalMachine: true);

int minutosInactividad = config.GetValue("Seguridad:MinutosInactividad", 20);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = ".VisorSIG.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(minutosInactividad);   // expira por inactividad (RF-SEG-05)
        o.SlidingExpiration = true;
        // Es una API: 401/403 en lugar de redirecciones.
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        // Un usuario bloqueado o con otro perfil pierde la sesión abierta (RF-SEG-03).
        o.Events.OnValidatePrincipal = ValidacionSesion.ValidarAsync;
    })
    // Aplicación móvil: token Bearer protegido con las mismas claves (sin cookie ni CSRF). Dura una jornada.
    .AddBearerToken(EsquemasAutenticacion.Movil, o => o.BearerTokenExpiration = TimeSpan.FromHours(12));

builder.Services.AddAuthorization(o =>
{
    // Todo endpoint exige sesión salvo [AllowAnonymous] (RF-SEG-04).
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.AddPolicy(RolesSistema.Administrador, p => p.RequireRole(RolesSistema.Administrador));
});

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = ".VisorSIG.Csrf";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.HttpOnly = true;
});

builder.Services.AddControllers(o => o.Filters.Add<ValidarCsrfFiltro>())
    // Parámetros con formato inválido (p. ej. pagina=abc o JSON mal formado): mensaje en español.
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx =>
        new BadRequestObjectResult(new ValidationProblemDetails(ctx.ModelState)
        {
            Title = "Solicitud inválida",
            Detail = "Algún dato de la solicitud no tiene el formato esperado.",
            Status = StatusCodes.Status400BadRequest,
        }) { ContentTypes = { "application/problem+json" } });
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ManejadorExcepciones>();
builder.Services.AddMemoryCache();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/salud", () => Results.Ok(new { estado = "ok", hora = DateTime.Now })).AllowAnonymous();
app.MapControllers();

// Precalentamiento de SQL Server en segundo plano. Tras días sin uso, Windows puede paginar la
// memoria del servicio ("A significant part of sql server process memory has been paged out") y la
// primera consulta espacial supera el tiempo límite. Así esa espera no la paga el primer usuario.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    for (int intento = 1; intento <= 3; intento++)
    {
        try
        {
            using var alcance = app.Services.CreateScope();
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            await alcance.ServiceProvider.GetRequiredService<ConsultaServicio>().ResumenAsync(app.Lifetime.ApplicationStopping);
            app.Logger.LogInformation("SQL Server listo en {Segundos:0.0} s (intento {Intento}).", reloj.Elapsed.TotalSeconds, intento);
            return;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            app.Logger.LogWarning("Precalentamiento de SQL Server, intento {Intento} sin éxito: {Mensaje}", intento, ex.Message);
        }
    }
}));

app.Run();

public partial class Program;
