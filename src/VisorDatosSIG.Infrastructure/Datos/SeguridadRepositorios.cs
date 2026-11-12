using System.Data;
using Dapper;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Infrastructure.Datos;

public sealed class UsuarioRepositorio(FabricaConexion fabrica) : IUsuarioRepositorio
{
    private const string ColumnasUsuario =
        "IdUsuario, Login, Nombre, PasswordHash, PasswordSalt, Iteraciones, Activo, FechaRegistro";

    // Plantilla de menú por perfil (Anexo E): Administrador todo; el resto, todo menos Administración.
    private const string AsignarMenu = """
        DELETE dbo.UsuarioMenu WHERE IdUsuario = @id;
        INSERT dbo.UsuarioMenu(IdUsuario, IdMenu, PuedeVer, PuedeCrear, PuedeEditar, PuedeEliminar)
        SELECT @id, m.IdMenu, 1, @admin, @admin, @admin
          FROM dbo.MenuOpciones m
          LEFT JOIN dbo.MenuOpciones p ON p.IdMenu = m.IdMenuPadre
         WHERE m.Estado = 1
           AND (@admin = 1
                OR (COALESCE(p.NombreMenu, m.NombreMenu) IN (N'Dashboard', N'Información geográfica', N'Cuenta', N'Ayuda')
                    AND m.NombreMenu <> N'Dashboard técnico'));
        """;   // misma regla que database/07_Operaciones.sql

    public Task<Usuario?> ObtenerPorLoginAsync(string login, CancellationToken ct) =>
        ObtenerAsync($"SELECT {ColumnasUsuario} FROM dbo.Usuarios WHERE Login = @valor", login, ct);

    public Task<Usuario?> ObtenerPorIdAsync(int idUsuario, CancellationToken ct) =>
        ObtenerAsync($"SELECT {ColumnasUsuario} FROM dbo.Usuarios WHERE IdUsuario = @valor", idUsuario, ct);

    private async Task<Usuario?> ObtenerAsync(string sql, object valor, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var fila = await cn.QuerySingleOrDefaultAsync<FilaUsuario>(new CommandDefinition(sql, new { valor }, cancellationToken: ct));
        if (fila is null) return null;
        var roles = await cn.QueryAsync<string>(new CommandDefinition("""
            SELECT r.NombreRol FROM dbo.UsuariosRoles ur JOIN dbo.Roles r ON r.IdRol = ur.IdRol
             WHERE ur.IdUsuario = @id AND r.Estado = 1 ORDER BY r.NombreRol
            """, new { id = fila.IdUsuario }, cancellationToken: ct));
        return new Usuario
        {
            IdUsuario = fila.IdUsuario, Login = fila.Login, Nombre = fila.Nombre, PasswordHash = fila.PasswordHash,
            PasswordSalt = fila.PasswordSalt, Iteraciones = fila.Iteraciones, Activo = fila.Activo,
            FechaRegistro = fila.FechaRegistro, Roles = roles.ToList(),
        };
    }

    public async Task<IReadOnlyList<UsuarioResumen>> ListarAsync(CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<(int IdUsuario, string Login, string Nombre, bool Activo, DateTime FechaRegistro, string? Roles)>(
            new CommandDefinition("""
                SELECT u.IdUsuario, u.Login, u.Nombre, u.Activo, u.FechaRegistro,
                       (SELECT STRING_AGG(r.NombreRol, ',') FROM dbo.UsuariosRoles ur JOIN dbo.Roles r ON r.IdRol = ur.IdRol
                         WHERE ur.IdUsuario = u.IdUsuario) AS Roles
                  FROM dbo.Usuarios u ORDER BY u.Login
                """, cancellationToken: ct));
        return filas.Select(f => new UsuarioResumen(f.IdUsuario, f.Login, f.Nombre, f.Activo, f.FechaRegistro,
            (f.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))).ToList();
    }

    public async Task<IReadOnlyList<RolDto>> ListarRolesAsync(CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        return (await cn.QueryAsync<RolDto>(new CommandDefinition(
            "SELECT IdRol, NombreRol, Descripcion FROM dbo.Roles WHERE Estado = 1 ORDER BY IdRol", cancellationToken: ct))).ToList();
    }

    public async Task<int> CrearAsync(string login, string nombre, byte[] hash, byte[] salt, int iteraciones, string rol, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        await cn.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var p = new DynamicParameters();
        p.Add("login", login, DbType.String, size: 50);
        p.Add("nombre", nombre, DbType.String, size: 120);
        p.Add("hash", hash, DbType.Binary, size: 32);
        p.Add("salt", salt, DbType.Binary, size: 32);
        p.Add("it", iteraciones);
        int id = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT dbo.Usuarios(Login, Nombre, PasswordHash, PasswordSalt, Iteraciones, Activo)
            OUTPUT INSERTED.IdUsuario VALUES(@login, @nombre, @hash, @salt, @it, 1);
            """, p, tx, cancellationToken: ct));
        await AsignarRolYMenuAsync(cn, tx, id, rol, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task ActualizarAsync(int idUsuario, string nombre, string rol, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        await cn.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await cn.ExecuteAsync(new CommandDefinition("UPDATE dbo.Usuarios SET Nombre = @nombre WHERE IdUsuario = @id",
            new { id = idUsuario, nombre }, tx, cancellationToken: ct));
        await AsignarRolYMenuAsync(cn, tx, idUsuario, rol, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task AsignarRolYMenuAsync(System.Data.Common.DbConnection cn, System.Data.Common.DbTransaction tx,
        int id, string rol, CancellationToken ct)
    {
        await cn.ExecuteAsync(new CommandDefinition("""
            DELETE dbo.UsuariosRoles WHERE IdUsuario = @id;
            INSERT dbo.UsuariosRoles(IdUsuario, IdRol) SELECT @id, IdRol FROM dbo.Roles WHERE NombreRol = @rol;
            """, new { id, rol }, tx, cancellationToken: ct));
        await cn.ExecuteAsync(new CommandDefinition(AsignarMenu,
            new { id, admin = rol == RolesSistema.Administrador }, tx, cancellationToken: ct));
    }

    public async Task CambiarEstadoAsync(int idUsuario, bool activo, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        await cn.ExecuteAsync(new CommandDefinition("UPDATE dbo.Usuarios SET Activo = @activo WHERE IdUsuario = @id",
            new { id = idUsuario, activo }, cancellationToken: ct));
    }

    public async Task CambiarContrasenaAsync(int idUsuario, byte[] hash, byte[] salt, int iteraciones, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var p = new DynamicParameters();
        p.Add("id", idUsuario);
        p.Add("hash", hash, DbType.Binary, size: 32);
        p.Add("salt", salt, DbType.Binary, size: 32);
        p.Add("it", iteraciones);
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Usuarios SET PasswordHash = @hash, PasswordSalt = @salt, Iteraciones = @it WHERE IdUsuario = @id",
            p, cancellationToken: ct));
    }

    public async Task<int> ContarAdministradoresActivosAsync(CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(DISTINCT u.IdUsuario) FROM dbo.Usuarios u
              JOIN dbo.UsuariosRoles ur ON ur.IdUsuario = u.IdUsuario
              JOIN dbo.Roles r ON r.IdRol = ur.IdRol
             WHERE u.Activo = 1 AND r.NombreRol = 'Administrador'
            """, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<OpcionMenu>> ObtenerMenuAsync(int idUsuario, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var opciones = (await cn.QueryAsync<OpcionMenuFila>(new CommandDefinition("""
            SELECT m.IdMenu, m.IdMenuPadre, m.Nivel, m.NombreMenu, m.Url, m.Icono, m.Orden
              FROM dbo.UsuarioMenu um JOIN dbo.MenuOpciones m ON m.IdMenu = um.IdMenu
             WHERE um.IdUsuario = @id AND um.PuedeVer = 1 AND m.Estado = 1
             ORDER BY m.Nivel, m.Orden
            """, new { id = idUsuario }, cancellationToken: ct)))
            .Select(f => new OpcionMenu(f.IdMenu, f.IdMenuPadre, f.Nivel, f.NombreMenu, f.Url, f.Icono, f.Orden)).ToList();
        var porId = opciones.ToDictionary(o => o.IdMenu);
        foreach (var o in opciones.Where(o => o.IdMenuPadre is not null))
            if (porId.TryGetValue(o.IdMenuPadre!.Value, out var padre)) padre.Hijos.Add(o);
        return opciones.Where(o => o.IdMenuPadre is null).OrderBy(o => o.Orden).ToList();
    }

    private sealed record FilaUsuario(int IdUsuario, string Login, string Nombre, byte[] PasswordHash, byte[] PasswordSalt,
        int Iteraciones, bool Activo, DateTime FechaRegistro);

    private sealed record OpcionMenuFila(int IdMenu, int? IdMenuPadre, int Nivel, string NombreMenu, string? Url, string? Icono, int Orden);
}

public sealed class BitacoraRepositorio(FabricaConexion fabrica) : IBitacoraRepositorio
{
    public async Task RegistrarAccesoAsync(int? idUsuario, string? login, string evento, bool exito, string? ip, string? detalle, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        var p = new DynamicParameters();
        p.Add("id", idUsuario);
        p.Add("login", Truncar(login, 50), DbType.String, size: 50);
        p.Add("evento", evento, DbType.String, size: 40);
        p.Add("exito", exito);
        p.Add("ip", Truncar(ip, 45), DbType.String, size: 45);
        p.Add("detalle", Truncar(detalle, 500), DbType.String, size: 500);
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.BitacoraAcceso(IdUsuario, Login, Evento, Exito, Ip, Detalle)
            VALUES(@id, @login, @evento, @exito, @ip, @detalle)
            """, p, cancellationToken: ct));
    }

    public async Task<int> ContarFallosRecientesAsync(string login, DateTime desde, CancellationToken ct)
    {
        await using var cn = fabrica.Crear();
        // Cuenta los fallos posteriores al último inicio exitoso dentro de la ventana.
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.BitacoraAcceso
             WHERE Login = @login AND Evento = N'LoginFallido' AND Fecha >= @desde
               AND Fecha > COALESCE((SELECT MAX(Fecha) FROM dbo.BitacoraAcceso
                                      WHERE Login = @login AND Evento = N'LoginExitoso'), '19000101')
            """, new { login, desde }, cancellationToken: ct));
    }

    public async Task<Pagina<RegistroAcceso>> ListarAccesosAsync(int pagina, int tamano, string? texto, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 5, 100);
        var p = new DynamicParameters();
        p.Add("off", (pagina - 1) * tamano);
        p.Add("tam", tamano);
        p.Add("t", string.IsNullOrWhiteSpace(texto) ? null : $"%{ConsultaCapasRepositorio.EscaparLike(texto.Trim())}%", DbType.String, size: 110);
        const string filtro = "(@t IS NULL OR Login LIKE @t ESCAPE N'\\' OR Evento LIKE @t ESCAPE N'\\' OR Detalle LIKE @t ESCAPE N'\\')";
        await using var cn = fabrica.Crear();
        await using var multi = await cn.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM dbo.BitacoraAcceso WHERE {filtro};
            SELECT IdBitacora, Fecha, Login, Evento, Exito, Ip, Detalle FROM dbo.BitacoraAcceso WHERE {filtro}
             ORDER BY Fecha DESC, IdBitacora DESC OFFSET @off ROWS FETCH NEXT @tam ROWS ONLY;
            """, p, cancellationToken: ct));
        int total = await multi.ReadSingleAsync<int>();
        var filas = (await multi.ReadAsync<RegistroAcceso>()).ToList();
        return new Pagina<RegistroAcceso>(total, pagina, tamano, filas);
    }

    private static string? Truncar(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
