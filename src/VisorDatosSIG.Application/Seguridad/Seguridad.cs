using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Application.Seguridad;

public interface IHasherContrasena
{
    int IteracionesPorDefecto { get; }
    (byte[] Hash, byte[] Salt, int Iteraciones) Generar(string contrasena);
    bool Verificar(string contrasena, byte[] hash, byte[] salt, int iteraciones);
}

public sealed record OpcionMenu(int IdMenu, int? IdMenuPadre, int Nivel, string Nombre, string? Url, string? Icono, int Orden)
{
    public List<OpcionMenu> Hijos { get; } = [];
}

public sealed record RolDto(int IdRol, string NombreRol, string? Descripcion);

public sealed record UsuarioResumen(int IdUsuario, string Login, string Nombre, bool Activo, DateTime FechaRegistro, IReadOnlyList<string> Roles)
{
    public string Perfil => RolesSistema.PerfilEfectivo(Roles);
}

public interface IUsuarioRepositorio
{
    Task<Usuario?> ObtenerPorLoginAsync(string login, CancellationToken ct);
    Task<Usuario?> ObtenerPorIdAsync(int idUsuario, CancellationToken ct);
    Task<IReadOnlyList<UsuarioResumen>> ListarAsync(CancellationToken ct);
    Task<IReadOnlyList<RolDto>> ListarRolesAsync(CancellationToken ct);
    Task<int> CrearAsync(string login, string nombre, byte[] hash, byte[] salt, int iteraciones, string rol, CancellationToken ct);
    Task ActualizarAsync(int idUsuario, string nombre, string rol, CancellationToken ct);
    Task CambiarEstadoAsync(int idUsuario, bool activo, CancellationToken ct);
    Task CambiarContrasenaAsync(int idUsuario, byte[] hash, byte[] salt, int iteraciones, CancellationToken ct);
    Task<int> ContarAdministradoresActivosAsync(CancellationToken ct);
    Task<IReadOnlyList<OpcionMenu>> ObtenerMenuAsync(int idUsuario, CancellationToken ct);
}

public sealed record RegistroAcceso(long IdBitacora, DateTime Fecha, string? Login, string Evento, bool Exito, string? Ip, string? Detalle);

public interface IBitacoraRepositorio
{
    Task RegistrarAccesoAsync(int? idUsuario, string? login, string evento, bool exito, string? ip, string? detalle, CancellationToken ct);
    Task<int> ContarFallosRecientesAsync(string login, DateTime desde, CancellationToken ct);
    Task<Pagina<RegistroAcceso>> ListarAccesosAsync(int pagina, int tamano, string? texto, CancellationToken ct);
}

/// <summary>Eventos que se registran en dbo.BitacoraAcceso (RF-SEG-06).</summary>
public static class EventosBitacora
{
    public const string LoginExitoso = "LoginExitoso";
    public const string LoginFallido = "LoginFallido";
    public const string LoginBloqueado = "LoginBloqueado";
    public const string CierreSesion = "CierreSesion";
    public const string CambioContrasena = "CambioContrasena";
    public const string UsuarioCreado = "UsuarioCreado";
    public const string UsuarioActualizado = "UsuarioActualizado";
    public const string UsuarioBloqueado = "UsuarioBloqueado";
    public const string UsuarioActivado = "UsuarioActivado";
    public const string ContrasenaRestablecida = "ContrasenaRestablecida";
    public const string ExportacionCsv = "ExportacionCsv";
    public const string MigracionEjecutada = "MigracionEjecutada";
    public const string AccesoDemo = "AccesoDemo";
    // Parte 2: operaciones de campo.
    public const string AsignacionGuardada = "AsignacionGuardada";
    public const string AsignacionAnulada = "AsignacionAnulada";
    public const string ConfiguracionOperativa = "ConfiguracionOperativa";
    public const string LoginMovil = "LoginMovil";
    public const string EjecucionRegistrada = "EjecucionRegistrada";
    public const string SimulacionRecorrido = "SimulacionRecorrido";
}

public sealed record ResultadoInicioSesion(bool Exito, Usuario? Usuario, string? Mensaje);

public sealed class AutenticacionServicio(IUsuarioRepositorio usuarios, IBitacoraRepositorio bitacora, IHasherContrasena hasher)
{
    public const int IntentosMaximos = 5;
    public static readonly TimeSpan VentanaBloqueo = TimeSpan.FromMinutes(15);
    private const string MensajeGenerico = "Usuario o contraseña incorrectos.";

    public async Task<ResultadoInicioSesion> IniciarSesionAsync(string? login, string? contrasena, string? ip, CancellationToken ct)
    {
        login = login?.Trim();
        if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(contrasena))
            return new(false, null, "Ingrese usuario y contraseña.");
        if (login.Length > 50) return new(false, null, MensajeGenerico);

        int fallos = await bitacora.ContarFallosRecientesAsync(login, DateTime.Now - VentanaBloqueo, ct);
        if (fallos >= IntentosMaximos)
        {
            await bitacora.RegistrarAccesoAsync(null, login, EventosBitacora.LoginBloqueado, false, ip,
                "Demasiados intentos fallidos", ct);
            return new(false, null, $"Demasiados intentos fallidos. Espere {VentanaBloqueo.TotalMinutes:0} minutos e intente de nuevo.");
        }

        var usuario = await usuarios.ObtenerPorLoginAsync(login, ct);
        bool valida = usuario is not null
            ? hasher.Verificar(contrasena, usuario.PasswordHash, usuario.PasswordSalt, usuario.Iteraciones)
            : VerificacionFicticia(contrasena);

        if (usuario is null || !valida)
        {
            await bitacora.RegistrarAccesoAsync(usuario?.IdUsuario, login, EventosBitacora.LoginFallido, false, ip,
                usuario is null ? "Usuario inexistente" : "Contraseña incorrecta", ct);
            return new(false, null, MensajeGenerico);
        }

        if (!usuario.Activo)
        {
            await bitacora.RegistrarAccesoAsync(usuario.IdUsuario, login, EventosBitacora.LoginFallido, false, ip,
                "Cuenta inactiva", ct);
            return new(false, null, "La cuenta está bloqueada. Contacte al administrador.");
        }

        await bitacora.RegistrarAccesoAsync(usuario.IdUsuario, usuario.Login, EventosBitacora.LoginExitoso, true, ip,
            $"Perfil {usuario.Perfil}", ct);
        return new(true, usuario, null);
    }

    public async Task CambiarContrasenaAsync(int idUsuario, string? actual, string? nueva, string? ip, CancellationToken ct)
    {
        var usuario = await usuarios.ObtenerPorIdAsync(idUsuario, ct)
            ?? throw new ErrorValidacion("El usuario no existe.");
        if (string.IsNullOrEmpty(actual) || !hasher.Verificar(actual, usuario.PasswordHash, usuario.PasswordSalt, usuario.Iteraciones))
            throw new ErrorValidacion("La contraseña actual no es correcta.", "actual");
        if (ReglasUsuario.ValidarContrasena(nueva) is { } error) throw new ErrorValidacion(error, "nueva");
        if (nueva == actual) throw new ErrorValidacion("La nueva contraseña debe ser distinta de la actual.", "nueva");

        var (hash, salt, iteraciones) = hasher.Generar(nueva!);
        await usuarios.CambiarContrasenaAsync(idUsuario, hash, salt, iteraciones, ct);
        await bitacora.RegistrarAccesoAsync(idUsuario, usuario.Login, EventosBitacora.CambioContrasena, true, ip, null, ct);
    }

    // Evita revelar por tiempo de respuesta si el usuario existe.
    private bool VerificacionFicticia(string contrasena)
    {
        hasher.Verificar(contrasena, new byte[32], new byte[32], hasher.IteracionesPorDefecto);
        return false;
    }
}

public sealed record DatosUsuario(string? Login, string? Nombre, string? Rol, string? Contrasena);

/// <summary>Administración de usuarios (solo perfil Administrador; la API lo exige en el servidor).</summary>
public sealed class UsuarioServicio(IUsuarioRepositorio usuarios, IBitacoraRepositorio bitacora, IHasherContrasena hasher)
{
    public Task<IReadOnlyList<UsuarioResumen>> ListarAsync(CancellationToken ct) => usuarios.ListarAsync(ct);
    public Task<IReadOnlyList<RolDto>> RolesAsync(CancellationToken ct) => usuarios.ListarRolesAsync(ct);

    public async Task<int> CrearAsync(DatosUsuario datos, Actor actor, CancellationToken ct)
    {
        if (ReglasUsuario.ValidarLogin(datos.Login) is { } e1) throw new ErrorValidacion(e1, "login");
        if (ReglasUsuario.ValidarNombre(datos.Nombre) is { } e2) throw new ErrorValidacion(e2, "nombre");
        if (ReglasUsuario.ValidarContrasena(datos.Contrasena) is { } e3) throw new ErrorValidacion(e3, "contrasena");
        var rol = await ValidarRolAsync(datos.Rol, ct);
        if (await usuarios.ObtenerPorLoginAsync(datos.Login!.Trim(), ct) is not null)
            throw new ErrorValidacion("Ya existe un usuario con ese nombre de usuario.", "login");

        var (hash, salt, iteraciones) = hasher.Generar(datos.Contrasena!);
        int id = await usuarios.CrearAsync(datos.Login.Trim(), datos.Nombre!.Trim(), hash, salt, iteraciones, rol, ct);
        await bitacora.RegistrarAccesoAsync(actor.IdUsuario, actor.Login, EventosBitacora.UsuarioCreado, true, actor.Ip,
            $"Usuario '{datos.Login.Trim()}' con rol {rol}", ct);
        return id;
    }

    public async Task ActualizarAsync(int idUsuario, DatosUsuario datos, Actor actor, CancellationToken ct)
    {
        var usuario = await ObtenerAsync(idUsuario, ct);
        if (ReglasUsuario.ValidarNombre(datos.Nombre) is { } e1) throw new ErrorValidacion(e1, "nombre");
        var rol = await ValidarRolAsync(datos.Rol, ct);
        if (usuario.EsAdministrador && rol != RolesSistema.Administrador)
            await ExigirOtroAdministradorAsync(idUsuario == actor.IdUsuario
                ? "No puede quitarse a sí mismo el perfil Administrador."
                : null, ct);

        await usuarios.ActualizarAsync(idUsuario, datos.Nombre!.Trim(), rol, ct);
        await bitacora.RegistrarAccesoAsync(actor.IdUsuario, actor.Login, EventosBitacora.UsuarioActualizado, true, actor.Ip,
            $"Usuario '{usuario.Login}': rol {rol}", ct);
    }

    public async Task CambiarEstadoAsync(int idUsuario, bool activo, Actor actor, CancellationToken ct)
    {
        var usuario = await ObtenerAsync(idUsuario, ct);
        if (!activo)
        {
            if (idUsuario == actor.IdUsuario) throw new ErrorValidacion("No puede bloquear su propia cuenta.");
            if (usuario.EsAdministrador) await ExigirOtroAdministradorAsync(null, ct);
        }
        await usuarios.CambiarEstadoAsync(idUsuario, activo, ct);
        await bitacora.RegistrarAccesoAsync(actor.IdUsuario, actor.Login,
            activo ? EventosBitacora.UsuarioActivado : EventosBitacora.UsuarioBloqueado, true, actor.Ip,
            $"Usuario '{usuario.Login}'", ct);
    }

    public async Task RestablecerContrasenaAsync(int idUsuario, string? nueva, Actor actor, CancellationToken ct)
    {
        var usuario = await ObtenerAsync(idUsuario, ct);
        if (ReglasUsuario.ValidarContrasena(nueva) is { } error) throw new ErrorValidacion(error, "contrasena");
        var (hash, salt, iteraciones) = hasher.Generar(nueva!);
        await usuarios.CambiarContrasenaAsync(idUsuario, hash, salt, iteraciones, ct);
        await bitacora.RegistrarAccesoAsync(actor.IdUsuario, actor.Login, EventosBitacora.ContrasenaRestablecida, true,
            actor.Ip, $"Usuario '{usuario.Login}'", ct);
    }

    private async Task<Usuario> ObtenerAsync(int idUsuario, CancellationToken ct) =>
        await usuarios.ObtenerPorIdAsync(idUsuario, ct) ?? throw new ErrorValidacion("El usuario no existe.");

    private async Task<string> ValidarRolAsync(string? rol, CancellationToken ct)
    {
        var roles = await usuarios.ListarRolesAsync(ct);
        return roles.FirstOrDefault(r => string.Equals(r.NombreRol, rol?.Trim(), StringComparison.OrdinalIgnoreCase))?.NombreRol
            ?? throw new ErrorValidacion("Seleccione un perfil válido.", "rol");
    }

    private async Task ExigirOtroAdministradorAsync(string? mensaje, CancellationToken ct)
    {
        if (mensaje is not null) throw new ErrorValidacion(mensaje);
        if (await usuarios.ContarAdministradoresActivosAsync(ct) <= 1)
            throw new ErrorValidacion("Debe existir al menos un Administrador activo.");
    }
}

/// <summary>Quién ejecuta una operación administrativa (para la bitácora).</summary>
public sealed record Actor(int IdUsuario, string Login, string? Ip);
