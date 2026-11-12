using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Api.Infraestructura;

/// <summary>Errores centralizados: mensajes claros para validación y nada de detalles internos para el resto (7.1).</summary>
public sealed class ManejadorExcepciones(ILogger<ManejadorExcepciones> log, IProblemDetailsService problemas) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        int estado;
        string titulo, detalle;
        switch (ex)
        {
            case ErrorValidacion v:
                estado = StatusCodes.Status400BadRequest; titulo = "Solicitud inválida"; detalle = v.Message; break;
            case OperationCanceledException when http.RequestAborted.IsCancellationRequested:
                return true;
            case Microsoft.Data.SqlClient.SqlException sql:
                log.LogError(sql, "Error de SQL Server en {Ruta}", http.Request.Path);
                estado = StatusCodes.Status503ServiceUnavailable; titulo = "Base de datos no disponible";
                detalle = "No se pudo consultar la base de datos. Intente nuevamente en unos segundos."; break;
            default:
                log.LogError(ex, "Error no controlado en {Ruta}", http.Request.Path);
                estado = StatusCodes.Status500InternalServerError; titulo = "Error interno";
                detalle = "Ocurrió un error inesperado. Intente nuevamente."; break;
        }
        http.Response.StatusCode = estado;
        var problema = new ProblemDetails { Status = estado, Title = titulo, Detail = detalle };
        if (ex is ErrorValidacion { Campo: { } campo }) problema.Extensions["campo"] = campo;
        return await problemas.TryWriteAsync(new() { HttpContext = http, ProblemDetails = problema, Exception = ex });
    }
}

/// <summary>Excluye un endpoint de la validación CSRF (solo el inicio de sesión).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SinCsrfAttribute : Attribute;

/// <summary>Protección CSRF para toda operación de escritura (POST/PUT/DELETE) con cabecera X-CSRF-TOKEN.</summary>
public sealed class ValidarCsrfFiltro(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var metodo = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(metodo) || HttpMethods.IsHead(metodo) || HttpMethods.IsOptions(metodo)) return;
        if (context.ActionDescriptor.EndpointMetadata.OfType<SinCsrfAttribute>().Any()) return;
        try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
        catch (AntiforgeryValidationException)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = 400, Title = "Solicitud no válida",
                Detail = "El token de seguridad (CSRF) no es válido o expiró. Recargue la página.",
            }) { StatusCode = 400 };
        }
    }
}

public static class UsuarioActual
{
    public const string ClaimNombre = "nombre";
    public const string ClaimPerfil = "perfil";
    public const string ClaimSello = "sello";

    public static string Sello(Usuario u) => Convert.ToBase64String(SHA256.HashData(u.PasswordSalt).AsSpan(0, 12));

    public static ClaimsPrincipal Crear(Usuario u, string esquema)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.IdUsuario.ToString()),
            new(ClaimTypes.Name, u.Login),
            new(ClaimNombre, u.Nombre),
            new(ClaimPerfil, u.Perfil),
            new(ClaimSello, Sello(u)),
        };
        claims.AddRange(u.Roles.Append(u.Perfil).Distinct().Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, esquema));
    }

    public static int Id(this ClaimsPrincipal p) => int.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    public static string Login(this ClaimsPrincipal p) => p.Identity?.Name ?? "";

    public static Actor Actor(this ControllerBase c) =>
        new(c.User.Id(), c.User.Login(), c.HttpContext.Connection.RemoteIpAddress?.ToString());

    public static object Datos(this ClaimsPrincipal p) => new
    {
        id = p.Id(),
        login = p.Login(),
        nombre = p.FindFirstValue(ClaimNombre),
        perfil = p.FindFirstValue(ClaimPerfil),
        roles = p.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().ToArray(),
    };
}

/// <summary>Resultado que escribe JSON/GeoJSON directamente con Utf8JsonWriter (sin materializar cadenas grandes).</summary>
public sealed class JsonEscritoResultado(Action<Utf8JsonWriter> escribir, string tipo = "application/json") : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        // Se arma en memoria: el serializador de NTS vacía de forma síncrona y Kestrel solo admite E/S asíncrona.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(64 * 1024);
        using (var w = new Utf8JsonWriter(buffer)) escribir(w);
        var r = context.HttpContext.Response;
        r.ContentType = $"{tipo}; charset=utf-8";
        r.ContentLength = buffer.WrittenCount;
        await r.Body.WriteAsync(buffer.WrittenMemory, context.HttpContext.RequestAborted);
    }
}
