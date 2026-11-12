using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using VisorDatosSIG.Api.Controllers;
using VisorDatosSIG.Api.Infraestructura;
using VisorDatosSIG.Application.Capas;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Consultas;
using VisorDatosSIG.Application.Migracion;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;
using VisorDatosSIG.Infrastructure.Datos;
using VisorDatosSIG.Infrastructure.Seguridad;

namespace VisorDatosSIG.Tests;

public class PruebasApi
{
    [Fact]
    public void Cookie_contiene_sello_derivado_de_la_sal_sin_exponerla()
    {
        var usuario = new Usuario { PasswordSalt = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray() };
        var principal = UsuarioActual.Crear(usuario, CookieAuthenticationDefaults.AuthenticationScheme);
        var esperado = Convert.ToBase64String(SHA256.HashData(usuario.PasswordSalt).AsSpan(0, 12));

        Assert.Equal(esperado, principal.FindFirstValue("sello"));
        Assert.DoesNotContain(Convert.ToBase64String(usuario.PasswordSalt), principal.Claims.Select(c => c.Value));
    }

    [Fact]
    public async Task Cookie_anterior_sin_sello_se_rechaza()
    {
        using var entorno = new EntornoSesion();
        var principal = entorno.CrearPrincipal();
        var identidad = Assert.IsType<ClaimsIdentity>(principal.Identity);
        identidad.RemoveClaim(identidad.FindFirst(UsuarioActual.ClaimSello)!);

        var validacion = entorno.Validacion(principal);
        await ValidacionSesion.ValidarAsync(validacion);

        Assert.Null(validacion.Principal);
        Assert.Equal(1, entorno.Autenticacion.Cierres);
    }

    [Fact]
    public async Task Cambiar_contrasena_conserva_sesion_actual_y_revoca_la_segunda_aunque_este_cacheada()
    {
        using var entorno = new EntornoSesion();
        var primera = entorno.CrearPrincipal();
        var segunda = entorno.CrearPrincipal();
        await ValidacionSesion.ValidarAsync(entorno.Validacion(segunda));
        var controlador = new CuentaController(entorno.Usuarios,
            new AutenticacionServicio(entorno.Usuarios, entorno.Bitacora, entorno.Hasher), entorno.Cache)
        {
            ControllerContext = new ControllerContext { HttpContext = entorno.Http(primera) },
        };

        var resultado = await controlador.CambiarContrasena(new("Original2026", "Renovada2026"), CancellationToken.None);

        Assert.IsType<NoContentResult>(resultado);
        var renovada = Assert.IsType<ClaimsPrincipal>(entorno.Autenticacion.PrincipalEmitido);
        Assert.NotEqual(segunda.FindFirstValue("sello"), renovada.FindFirstValue("sello"));
        var vigente = entorno.Validacion(renovada);
        await ValidacionSesion.ValidarAsync(vigente);
        Assert.NotNull(vigente.Principal);
        var anterior = entorno.Validacion(segunda);
        await ValidacionSesion.ValidarAsync(anterior);
        Assert.Null(anterior.Principal);
    }

    [Fact]
    public async Task Restablecer_contrasena_revoca_todas_las_sesiones_aunque_esten_cacheadas()
    {
        using var entorno = new EntornoSesion();
        var primera = entorno.CrearPrincipal();
        var segunda = entorno.CrearPrincipal();
        await ValidacionSesion.ValidarAsync(entorno.Validacion(primera));
        var controlador = new UsuariosController(
            new UsuarioServicio(entorno.Usuarios, entorno.Bitacora, entorno.Hasher), entorno.Cache)
        {
            ControllerContext = new ControllerContext { HttpContext = entorno.Http(primera) },
        };

        Assert.IsType<NoContentResult>(await controlador.Restablecer(7, new("Restablecida2026"), CancellationToken.None));

        foreach (var principal in new[] { primera, segunda })
        {
            var validacion = entorno.Validacion(principal);
            await ValidacionSesion.ValidarAsync(validacion);
            Assert.Null(validacion.Principal);
        }
        Assert.Null(entorno.Autenticacion.PrincipalEmitido);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"campo\":\"Estado\",\"operador\":\"igual\",\"valor\":\"1\"},null]")]
    public async Task Filtro_nulo_da_error_de_validacion_antes_de_consultar_datos(string filtros)
    {
        var controlador = new BusquedaController(null!, null!);
        var error = await Assert.ThrowsAsync<ErrorValidacion>(() => controlador.Buscar("codigosfijos", filtros, null));
        Assert.Equal("filtros", error.Campo);
        Assert.Contains("nulos", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    [InlineData(int.MaxValue)]
    public async Task Paginas_fuera_de_rango_se_rechazan_antes_de_consultar_bitacora_y_migraciones(int pagina)
    {
        var bitacora = new BitacoraController(null!);
        var migraciones = new MigracionesController(null!);
        var errorBitacora = await Assert.ThrowsAsync<ErrorValidacion>(() => bitacora.Listar(pagina));
        var errorMigracion = await Assert.ThrowsAsync<ErrorValidacion>(() => migraciones.Listar(pagina));
        Assert.Equal("pagina", errorBitacora.Campo);
        Assert.Equal("pagina", errorMigracion.Campo);
    }

    [Fact]
    public async Task Salud_sin_conexion_responde_503_sin_detalles_internos()
    {
        var controlador = new SaludController(new FabricaConexion(""));
        var respuesta = Assert.IsType<ObjectResult>(await controlador.BaseDatos(CancellationToken.None));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, respuesta.StatusCode);
        Assert.Equal("{\"estado\":\"no disponible\"}", JsonSerializer.Serialize(respuesta.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalogo_incluye_cinco_estados_aunque_no_haya_filas_y_cachea_los_conteos(bool baseVacia)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repositorio = new RepositorioCatalogoSimulado();
        if (!baseVacia)
        {
            repositorio.Conteos[1] = 4;
            repositorio.Conteos[3] = 9;
        }
        var controlador = new CapasController(new ConsultaServicio(repositorio), repositorio, cache);

        var primera = Assert.IsType<OkObjectResult>(await controlador.Catalogo(CancellationToken.None));
        var datos = JsonSerializer.SerializeToElement(primera.Value).GetProperty("conteoPorEstado");
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, datos.EnumerateObject().Select(p => p.Name));
        Assert.Equal(baseVacia ? 0 : 4, datos.GetProperty("1").GetInt32());
        Assert.Equal(baseVacia ? 0 : 9, datos.GetProperty("3").GetInt32());
        foreach (var estado in new[] { "2", "4", "5" }) Assert.Equal(0, datos.GetProperty(estado).GetInt32());

        repositorio.Conteos[1] = 99;
        var segunda = Assert.IsType<OkObjectResult>(await controlador.Catalogo(CancellationToken.None));
        var cacheados = JsonSerializer.SerializeToElement(segunda.Value).GetProperty("conteoPorEstado");
        Assert.Equal(baseVacia ? 0 : 4, cacheados.GetProperty("1").GetInt32());
        Assert.Equal(1, repositorio.ConsultasConteo);
    }

    private sealed class EntornoSesion : IDisposable
    {
        public readonly HasherPbkdf2 Hasher = new();
        public readonly UsuariosSimulados Usuarios;
        public readonly BitacoraSimulada Bitacora = new();
        public readonly AutenticacionSimulada Autenticacion = new();
        public readonly MemoryCache Cache = new(new MemoryCacheOptions());
        private readonly ServiceProvider _servicios;

        public EntornoSesion()
        {
            var (hash, salt, iteraciones) = Hasher.Generar("Original2026");
            Usuarios = new UsuariosSimulados(new Usuario
            {
                IdUsuario = 7, Login = "prueba", Nombre = "Usuario de prueba", Activo = true,
                PasswordHash = hash, PasswordSalt = salt, Iteraciones = iteraciones, Roles = [RolesSistema.Administrador],
            });
            _servicios = new ServiceCollection()
                .AddSingleton<IUsuarioRepositorio>(Usuarios)
                .AddSingleton<IMemoryCache>(Cache)
                .AddSingleton<IAuthenticationService>(Autenticacion)
                .BuildServiceProvider();
        }

        public ClaimsPrincipal CrearPrincipal() => UsuarioActual.Crear(Usuarios.Usuario, CookieAuthenticationDefaults.AuthenticationScheme);
        public DefaultHttpContext Http(ClaimsPrincipal principal) => new() { User = principal, RequestServices = _servicios };
        public CookieValidatePrincipalContext Validacion(ClaimsPrincipal principal) => new(
            Http(principal),
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme));

        public void Dispose()
        {
            _servicios.Dispose();
            Cache.Dispose();
        }
    }

    private sealed class AutenticacionSimulada : IAuthenticationService
    {
        public ClaimsPrincipal? PrincipalEmitido;
        public int Cierres;
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            PrincipalEmitido = principal;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            Cierres++;
            return Task.CompletedTask;
        }
    }

    private sealed class UsuariosSimulados(Usuario usuario) : IUsuarioRepositorio
    {
        public Usuario Usuario { get; } = usuario;
        public Task<Usuario?> ObtenerPorIdAsync(int id, CancellationToken ct) => Task.FromResult<Usuario?>(id == Usuario.IdUsuario ? Usuario : null);
        public Task<Usuario?> ObtenerPorLoginAsync(string login, CancellationToken ct) => Task.FromResult<Usuario?>(login == Usuario.Login ? Usuario : null);
        public Task CambiarContrasenaAsync(int id, byte[] hash, byte[] salt, int iteraciones, CancellationToken ct)
        {
            Usuario.PasswordHash = hash;
            Usuario.PasswordSalt = salt;
            Usuario.Iteraciones = iteraciones;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<UsuarioResumen>> ListarAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RolDto>> ListarRolesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CrearAsync(string login, string nombre, byte[] hash, byte[] salt, int iteraciones, string rol, CancellationToken ct) => throw new NotSupportedException();
        public Task ActualizarAsync(int id, string nombre, string rol, CancellationToken ct) => throw new NotSupportedException();
        public Task CambiarEstadoAsync(int id, bool activo, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ContarAdministradoresActivosAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<OpcionMenu>> ObtenerMenuAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class BitacoraSimulada : IBitacoraRepositorio
    {
        public Task RegistrarAccesoAsync(int? id, string? login, string evento, bool exito, string? ip, string? detalle, CancellationToken ct) => Task.CompletedTask;
        public Task<int> ContarFallosRecientesAsync(string login, DateTime desde, CancellationToken ct) => Task.FromResult(0);
        public Task<Pagina<RegistroAcceso>> ListarAccesosAsync(int pagina, int tamano, string? texto, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RepositorioCatalogoSimulado : IConsultaCapasRepositorio, IMigracionRepositorio
    {
        public Dictionary<int, int> Conteos { get; } = [];
        public int ConsultasConteo;
        public Task<IReadOnlyList<ResumenCapa>> ObtenerResumenAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ResumenCapa>>([]);
        public Task<DateTime?> UltimaActualizacionAsync(CancellationToken ct) => Task.FromResult<DateTime?>(null);
        public Task<IReadOnlyDictionary<int, int>> ObtenerConteoPorEstadoAsync(CancellationToken ct)
        {
            ConsultasConteo++;
            return Task.FromResult<IReadOnlyDictionary<int, int>>(Conteos);
        }
        public Task<(IReadOnlyList<EntidadGeo> Entidades, bool Truncado)> ObtenerPorExtensionAsync(CapaDefinicion capa, Envelope bbox, double tolerancia, int limite, CancellationToken ct) => throw new NotSupportedException();
        public Task<EntidadGeo?> ObtenerPorIdAsync(CapaDefinicion capa, int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntidadGeo>> IdentificarAsync(CapaDefinicion capa, double lon, double lat, double tolerancia, int limite, CancellationToken ct) => throw new NotSupportedException();
        public Task<ResultadoBusqueda> BuscarAsync(CapaDefinicion capa, SolicitudBusqueda solicitud, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntidadGeo>> ExportarAsync(CapaDefinicion capa, SolicitudBusqueda solicitud, int maximo, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntidadGeo>> BuscarInmueblesAsync(string texto, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EntidadGeo>> BuscarTextoAsync(CapaDefinicion capa, string texto, int limite, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ProbarConexionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<int> MarcarInterrumpidasAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<int> IniciarBitacoraAsync(EntradaBitacoraMigracion entrada, CancellationToken ct) => throw new NotSupportedException();
        public Task FinalizarBitacoraAsync(int id, string estado, ConteosCarga conteos, int duracion, string? mensaje, CancellationToken ct) => throw new NotSupportedException();
        public Task RegistrarIncidenciasAsync(int id, IEnumerable<Incidencia> incidencias, CancellationToken ct) => throw new NotSupportedException();
        public Task<IMigracionTransaccion> IniciarTransaccionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ReconstruirIndicesEspacialesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Pagina<MigracionHistorial>> ListarHistorialAsync(int pagina, int tamano, CancellationToken ct) => throw new NotSupportedException();
        public Task<(MigracionHistorial? Cabecera, IReadOnlyList<DetalleMigracion> Detalle)> ObtenerAsync(int id, CancellationToken ct) => throw new NotSupportedException();
    }
}
