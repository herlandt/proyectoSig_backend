using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using VisorDatosSIG.Api.Controllers;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Tests;

/// <summary>El acceso rápido de demostración no debe existir fuera de Development ni sin habilitarlo.</summary>
public class PruebasAccesoDemo
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    public async Task Sin_Development_o_sin_habilitar_responde_404(string entorno, bool habilitado)
    {
        var p = new Preparacion(entorno, habilitado);

        Assert.IsType<NotFoundResult>(p.Controlador.Perfiles());
        Assert.IsType<NotFoundResult>(await p.Controlador.Ingresar(new("Administrador"), CancellationToken.None));
        Assert.Null(p.Autenticacion.Emitido);
        Assert.Empty(p.Bitacora.Eventos);
    }

    [Fact]
    public async Task En_Development_habilitado_inicia_la_sesion_configurada_y_la_registra()
    {
        var p = new Preparacion("Development", true);

        var perfiles = Assert.IsType<OkObjectResult>(p.Controlador.Perfiles());
        Assert.Contains("Administrador", (IEnumerable<string>)perfiles.Value!.GetType().GetProperty("perfiles")!.GetValue(perfiles.Value)!);

        Assert.IsType<OkObjectResult>(await p.Controlador.Ingresar(new("administrador"), CancellationToken.None));
        Assert.Equal("demo.admin", p.Autenticacion.Emitido?.Identity?.Name);
        Assert.Equal([EventosBitacora.AccesoDemo], p.Bitacora.Eventos);
    }

    [Fact]
    public async Task Perfil_desconocido_es_error_de_validacion()
    {
        var p = new Preparacion("Development", true);
        await Assert.ThrowsAsync<ErrorValidacion>(() => p.Controlador.Ingresar(new("Superusuario"), CancellationToken.None));
    }

    [Fact]
    public async Task Cuenta_bloqueada_no_inicia_sesion()
    {
        var p = new Preparacion("Development", true, activo: false);
        var r = Assert.IsType<ObjectResult>(await p.Controlador.Ingresar(new("Administrador"), CancellationToken.None));
        Assert.Equal(StatusCodes.Status401Unauthorized, r.StatusCode);
        Assert.Null(p.Autenticacion.Emitido);
    }

    private sealed class Preparacion
    {
        public readonly AutenticacionFalsa Autenticacion = new();
        public readonly BitacoraFalsa Bitacora = new();
        public readonly AccesoDemoController Controlador;

        public Preparacion(string entorno, bool habilitado, bool activo = true)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Habilitado"] = habilitado.ToString(),
                ["Demo:Cuentas:Administrador"] = "demo.admin",
            }).Build();
            var usuario = new Usuario
            {
                IdUsuario = 9, Login = "demo.admin", Nombre = "Demo", Activo = activo,
                PasswordSalt = new byte[32], Roles = [RolesSistema.Administrador],
            };
            var coleccion = new ServiceCollection().AddSingleton<IAuthenticationService>(Autenticacion);
            coleccion.AddControllers();   // registra ProblemDetailsFactory, como en la API real
            var servicios = coleccion.BuildServiceProvider();
            Controlador = new AccesoDemoController(new EntornoFalso(entorno), config, new UsuariosFalsos(usuario), Bitacora,
                new MemoryCache(new MemoryCacheOptions()))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = servicios } },
            };
        }
    }

    private sealed class EntornoFalso(string nombre) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "VisorDatosSIG.Api";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AutenticacionFalsa : IAuthenticationService
    {
        public ClaimsPrincipal? Emitido;
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            Emitido = principal;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class BitacoraFalsa : IBitacoraRepositorio
    {
        public List<string> Eventos { get; } = [];
        public Task RegistrarAccesoAsync(int? id, string? login, string evento, bool exito, string? ip, string? detalle, CancellationToken ct)
        {
            Eventos.Add(evento);
            return Task.CompletedTask;
        }
        public Task<int> ContarFallosRecientesAsync(string login, DateTime desde, CancellationToken ct) => Task.FromResult(0);
        public Task<Pagina<RegistroAcceso>> ListarAccesosAsync(int pagina, int tamano, string? texto, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class UsuariosFalsos(Usuario usuario) : IUsuarioRepositorio
    {
        public Task<Usuario?> ObtenerPorLoginAsync(string login, CancellationToken ct) =>
            Task.FromResult<Usuario?>(string.Equals(login, usuario.Login, StringComparison.OrdinalIgnoreCase) ? usuario : null);
        public Task<Usuario?> ObtenerPorIdAsync(int id, CancellationToken ct) => Task.FromResult<Usuario?>(id == usuario.IdUsuario ? usuario : null);
        public Task<IReadOnlyList<UsuarioResumen>> ListarAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RolDto>> ListarRolesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CrearAsync(string login, string nombre, byte[] hash, byte[] salt, int iteraciones, string rol, CancellationToken ct) => throw new NotSupportedException();
        public Task ActualizarAsync(int idUsuario, string nombre, string rol, CancellationToken ct) => throw new NotSupportedException();
        public Task CambiarEstadoAsync(int idUsuario, bool activo, CancellationToken ct) => throw new NotSupportedException();
        public Task CambiarContrasenaAsync(int idUsuario, byte[] hash, byte[] salt, int iteraciones, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ContarAdministradoresActivosAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<OpcionMenu>> ObtenerMenuAsync(int idUsuario, CancellationToken ct) => throw new NotSupportedException();
    }
}
