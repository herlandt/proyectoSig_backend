using System.Globalization;
using System.Xml.Linq;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Migracion;
using VisorDatosSIG.Application.Operaciones;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Domain;
using VisorDatosSIG.Infrastructure.Datos;
using VisorDatosSIG.Infrastructure.Geo;
using VisorDatosSIG.Infrastructure.Seguridad;

// Herramientas de administración de VisorDatosSIG (instalación, pruebas y automatización).
// La cadena de conexión y las contraseñas nunca van en el código: se leen de
// --conexion / VISOR_CONEXION y de VISOR_PASSWORD (o se piden por consola sin eco).
Console.OutputEncoding = System.Text.Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("es-BO");

var argumentos = Argumentos.Parsear(args);
try
{
    return argumentos.Comando switch
    {
        "validar" => Comandos.Validar(argumentos),
        "migrar" => await Comandos.MigrarAsync(argumentos),
        "crear-usuario" => await Comandos.CrearUsuarioAsync(argumentos),
        "reconstruir-indices" => await Comandos.ReconstruirIndicesAsync(argumentos),
        "exportar" => await Comandos.ExportarAsync(argumentos),
        "simular-recorrido" => await Comandos.SimularRecorridoAsync(argumentos),
        _ => Comandos.Ayuda(),
    };
}
catch (ErrorValidacion ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 2;
}

internal sealed record Argumentos(string Comando, Dictionary<string, string> Opciones)
{
    public static Argumentos Parsear(string[] args)
    {
        var opciones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
            if (args[i].StartsWith("--", StringComparison.Ordinal))
                opciones[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        return new(args.Length > 0 ? args[0].ToLowerInvariant() : "", opciones);
    }

    public string? Opcion(string nombre) => Opciones.TryGetValue(nombre, out var v) ? v : null;
    public string Requerida(string nombre) => Opcion(nombre) ?? throw new ErrorValidacion($"Falta la opción --{nombre}.");
    public string Conexion() => Opcion("conexion") ?? Environment.GetEnvironmentVariable("VISOR_CONEXION")
        ?? throw new ErrorValidacion("Indique la conexión con --conexion o la variable de entorno VISOR_CONEXION.");
}

internal static class Comandos
{
    public static int Ayuda()
    {
        Console.WriteLine("""
            VisorDatosSIG · Herramientas
              validar              --carpeta <ruta>                         Valida los SHP sin tocar la base
              migrar               --carpeta <ruta> --usuario <login> [--modo reemplazar|anexar] [--capas manzanas,lotes,codigosfijos,vias]
              crear-usuario        --login <login> --nombre "<nombre>" --rol <Administrador|Consultor|...>
              reconstruir-indices                                          Recrea los índices espaciales con caja ajustada
              exportar             --carpeta <ruta>                         Exporta las 4 tablas de SQL Server a Shapefile (WGS 84)
              simular-recorrido    --trabajador <login> [--tipo corte|reconexion] [--paradas N]
                                   [--tiempo-real [--acelerar N]] [--gpx <archivo.gpx>]
                                   Posiciones GPS sobre la ruta activa para demostrar el monitoreo: por defecto el
                                   recorrido termina ahora; --tiempo-real las envía al ritmo del GPS; --gpx solo
                                   escribe el archivo para reproducirlo en el emulador de Android (no toca la base)
            Conexión: --conexion "<cadena>" o variable VISOR_CONEXION. Contraseña: variable VISOR_PASSWORD o se solicita.
            """);
        return 1;
    }

    public static int Validar(Argumentos a)
    {
        var validador = new ValidadorShapefile(new LectorShapefileNts());
        bool bloqueada = false;
        foreach (var (def, archivos) in DefinicionesMigracion.DetectarEnCarpeta(a.Requerida("carpeta")))
        {
            if (def is null) { Console.WriteLine($"- {archivos.Nombre}: no se reconoce la capa por el nombre (se ignora)."); continue; }
            var r = validador.Validar(def, archivos, confirmarSinPrj: false);
            bloqueada |= r.Bloqueada;
            Console.WriteLine($"\n== {def.Nombre} ← {archivos.Nombre}");
            if (r.Info is { } i)
                Console.WriteLine($"   Tipo {i.TipoGeometria} · {i.Registros} registros · codificación {i.Codificacion} · campos: {string.Join(", ", i.Campos.Select(c => c.Nombre))}");
            Console.WriteLine($"   Leídos {r.Leidos} · válidos {r.Validos} · fallidos {r.Fallidos} · duplicados {r.Duplicados} · reparados {r.Reparados} · {(r.Bloqueada ? "BLOQUEADA" : "lista para migrar")}");
            foreach (var inc in r.Incidencias.Resumen())
                Console.WriteLine($"   [{inc.Nivel}] {inc.Codigo}: {inc.Mensaje}");
        }
        return bloqueada ? 3 : 0;
    }

    public static async Task<int> MigrarAsync(Argumentos a)
    {
        // Validar antes de leer la conexión o abrir SQL Server: un error de escritura nunca reemplaza datos.
        var modo = ModosCarga.Parsear(a.Opcion("modo"));
        var fabrica = new FabricaConexion(a.Conexion());
        var repoMigracion = new MigracionRepositorio(fabrica);
        if (await repoMigracion.ProbarConexionAsync(CancellationToken.None) is { } errorConexion)
            throw new ErrorValidacion(errorConexion);

        // El operador se autentica con un usuario de la aplicación (perfil Administrador o Catastro).
        var login = a.Requerida("usuario");
        var auth = new AutenticacionServicio(new UsuarioRepositorio(fabrica), new BitacoraRepositorio(fabrica), new HasherPbkdf2());
        var resultado = await auth.IniciarSesionAsync(login, Contrasena(), "herramientas", CancellationToken.None);
        if (!resultado.Exito) throw new ErrorValidacion(resultado.Mensaje ?? "Autenticación rechazada.");
        if (!resultado.Usuario!.Roles.Any(r => r is RolesSistema.Administrador or "Catastro"))
            throw new ErrorValidacion("Solo los perfiles Administrador o Catastro pueden migrar datos.");

        var filtro = a.Opcion("capas")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var capas = DefinicionesMigracion.DetectarEnCarpeta(a.Requerida("carpeta"))
            .Where(t => t.Definicion is not null && (filtro is null || filtro.Contains(t.Definicion.Id, StringComparer.OrdinalIgnoreCase)))
            .Select(t => new CapaAMigrar(t.Definicion!, t.Archivos))
            .ToList();

        var servicio = new MigracionServicio(new LectorShapefileNts(), repoMigracion);
        string ultimaFase = "";
        var progreso = new Progress<ProgresoMigracion>(p =>
        {
            var linea = $"{p.Capa} · {p.Fase}" + (p.Total > 0 ? $" {p.Procesados}/{p.Total} (ok {p.Exitosos}, omitidos {p.Omitidos}, fallidos {p.Fallidos})" : "");
            if (linea != ultimaFase) Console.WriteLine(linea);
            ultimaFase = linea;
        });

        using var cancelacion = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelacion.Cancel(); Console.WriteLine("Cancelando..."); };
        var resumen = await servicio.EjecutarAsync(
            new SolicitudMigracion(resultado.Usuario.Login, Environment.MachineName, modo, capas), progreso, cancelacion.Token);

        Console.WriteLine($"\nResultado: {resumen.Estado} en {resumen.Duracion.TotalSeconds:0.0} s — {resumen.Mensaje}");
        foreach (var c in resumen.Capas)
            Console.WriteLine($"  {c.Definicion.Nombre,-14} leídos {c.Conteos.Leidos,6} · insertados {c.Conteos.Insertados,6} · omitidos {c.Conteos.Omitidos,4} · fallidos {c.Conteos.Fallidos,3} · reparados {c.Conteos.Reparados} · bitácora #{c.IdMigracion}");
        if (resumen.Vinculos is { } v)
            Console.WriteLine($"  Vínculos: códigos con lote {v.CodigosConLote}/{v.TotalCodigos} · lotes con manzana {v.LotesConManzana}/{v.TotalLotes} · geometrías reparadas en SQL {resumen.GeometriasReparadasEnSql}");
        return resumen.Exitosa ? 0 : 4;
    }

    public static async Task<int> CrearUsuarioAsync(Argumentos a)
    {
        var fabrica = new FabricaConexion(a.Conexion());
        var usuarios = new UsuarioRepositorio(fabrica);
        var hasher = new HasherPbkdf2();
        string login = a.Requerida("login"), nombre = a.Requerida("nombre"), rol = a.Opcion("rol") ?? RolesSistema.Consultor;
        if (ReglasUsuario.ValidarLogin(login) is { } e1) throw new ErrorValidacion(e1);
        if (ReglasUsuario.ValidarNombre(nombre) is { } e2) throw new ErrorValidacion(e2);
        var contrasena = Contrasena();
        if (ReglasUsuario.ValidarContrasena(contrasena) is { } e3) throw new ErrorValidacion(e3);
        if (!(await usuarios.ListarRolesAsync(CancellationToken.None)).Any(r => string.Equals(r.NombreRol, rol, StringComparison.OrdinalIgnoreCase)))
            throw new ErrorValidacion($"El rol '{rol}' no existe.");

        var existente = await usuarios.ObtenerPorLoginAsync(login, CancellationToken.None);
        var (hash, salt, iteraciones) = hasher.Generar(contrasena);
        if (existente is null)
        {
            int id = await usuarios.CrearAsync(login, nombre, hash, salt, iteraciones, rol, CancellationToken.None);
            Console.WriteLine($"Usuario '{login}' creado (Id {id}, rol {rol}).");
        }
        else
        {
            await usuarios.ActualizarAsync(existente.IdUsuario, nombre, rol, CancellationToken.None);
            await usuarios.CambiarContrasenaAsync(existente.IdUsuario, hash, salt, iteraciones, CancellationToken.None);
            await usuarios.CambiarEstadoAsync(existente.IdUsuario, true, CancellationToken.None);
            Console.WriteLine($"Usuario '{login}' actualizado (rol {rol}, contraseña restablecida, activo).");
        }
        await new BitacoraRepositorio(fabrica).RegistrarAccesoAsync(null, login, EventosBitacora.UsuarioCreado, true, "herramientas",
            $"Alta/actualización por herramienta de instalación ({rol})", CancellationToken.None);
        return 0;
    }

    public static async Task<int> ReconstruirIndicesAsync(Argumentos a)
    {
        var caja = await new MigracionRepositorio(new FabricaConexion(a.Conexion())).ReconstruirIndicesEspacialesAsync(CancellationToken.None);
        Console.WriteLine($"Índices espaciales reconstruidos: {caja}");
        return 0;
    }

    public static async Task<int> ExportarAsync(Argumentos a)
    {
        var carpeta = Path.GetFullPath(a.Requerida("carpeta"));
        var conteos = await new ExportadorShapefile(new FabricaConexion(a.Conexion())).ExportarAsync(carpeta, CancellationToken.None);
        foreach (var (capa, n) in conteos) Console.WriteLine($"  {capa,-18} {n,7:N0} entidades");
        Console.WriteLine($"Shapefiles exportados en {carpeta}");
        return 0;
    }

    public static async Task<int> SimularRecorridoAsync(Argumentos a)
    {
        int? Entero(string nombre) => a.Opcion(nombre) is { } t
            ? int.TryParse(t, out var n) && n > 0 ? n : throw new ErrorValidacion($"--{nombre} debe ser un número positivo.")
            : null;
        int? paradas = Entero("paradas");
        int acelerar = Entero("acelerar") ?? 1;
        TipoAsignacion[] tipos = a.Opcion("tipo") is { } texto
            ? [TiposAsignacion.Parsear(texto) ?? throw new ErrorValidacion("--tipo debe ser corte o reconexion.")]
            : [TipoAsignacion.Corte, TipoAsignacion.Reconexion];

        var fabrica = new FabricaConexion(a.Conexion());
        var login = a.Requerida("trabajador");
        var usuario = await new UsuarioRepositorio(fabrica).ObtenerPorLoginAsync(login, CancellationToken.None)
            ?? throw new ErrorValidacion($"El usuario '{login}' no existe.");
        var repo = new OperacionesRepositorio(fabrica);
        AsignacionActiva? asignacion = null;
        foreach (var tipo in tipos)
            asignacion ??= await repo.ObtenerActivaAsync(usuario.IdUsuario, tipo, CancellationToken.None);
        if (asignacion is null || !asignacion.Paradas.Any(p => p.Estado == EstadosParada.Pendiente))
            throw new ErrorValidacion($"'{login}' no tiene una ruta activa con paradas pendientes. Asígnela primero desde el visor web.");

        // Acelerado: el trabajador avanza N veces más rápido, pero las posiciones llevan la hora real.
        var configuracion = await repo.ObtenerConfiguracionAsync(CancellationToken.None);
        configuracion = configuracion with
        {
            VelocidadKmH = configuracion.VelocidadKmH * acelerar,
            MinutosPorParada = configuracion.MinutosPorParada / acelerar,
        };
        bool tiempoReal = a.Opcion("tiempo-real") is not null;
        var posiciones = SimuladorRecorrido.Generar(asignacion, configuracion, DateTime.Now, paradas);
        if (posiciones.Count == 0) throw new ErrorValidacion("La ruta no tiene recorrido que simular.");
        var duracion = posiciones[^1].FechaHora - posiciones[0].FechaHora;
        Console.WriteLine($"Ruta {asignacion.IdAsignacion} ({asignacion.Tipo.Nombre()}) de {login}: {posiciones.Count} posiciones, " +
                          $"{duracion.TotalMinutes:0} min a {configuracion.VelocidadKmH:0.#} km/h.");

        if (a.Opcion("gpx") is { } archivo)
        {
            var ruta = Path.GetFullPath(archivo);
            EscribirGpx(ruta, posiciones, $"Ruta {asignacion.IdAsignacion} · {login}");
            Console.WriteLine($"GPX escrito en {ruta}. En el emulador: ⋮ > Location > Routes > Import GPX/KML.");
            return 0;
        }

        if (!tiempoReal)   // el recorrido completo termina ahora: se ve de inmediato en «Recorridos del día»
            posiciones = posiciones.Select(p => p with { FechaHora = p.FechaHora - duracion }).ToList();
        if (posiciones[0].FechaHora.Date != DateTime.Today)
            Console.WriteLine("Aviso: el recorrido empieza el día anterior; use --paradas para acortarlo.");
        await repo.IniciarRecorridoAsync(asignacion.IdAsignacion, usuario.IdUsuario, CancellationToken.None);
        await new BitacoraRepositorio(fabrica).RegistrarAccesoAsync(null, login, EventosBitacora.SimulacionRecorrido, true,
            "herramientas", $"Ruta {asignacion.IdAsignacion}: {posiciones.Count} posiciones simuladas{(tiempoReal ? " en tiempo real" : "")}", CancellationToken.None);

        using var cancelacion = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelacion.Cancel(); };
        int guardadas = 0;
        try
        {
            if (!tiempoReal)
                foreach (var lote in posiciones.Chunk(CampoServicio.MaximoPosicionesPorEnvio))
                    guardadas += await repo.RegistrarPosicionesAsync(usuario.IdUsuario, asignacion.IdAsignacion, lote, cancelacion.Token);
            else
                foreach (var p in posiciones)
                {
                    var espera = p.FechaHora - DateTime.Now;
                    if (espera > TimeSpan.Zero) await Task.Delay(espera, cancelacion.Token);
                    guardadas += await repo.RegistrarPosicionesAsync(usuario.IdUsuario, asignacion.IdAsignacion, [p], cancelacion.Token);
                    Console.WriteLine($"  {p.FechaHora:HH:mm:ss}  {p.Lat:0.000000}  {p.Lon:0.000000}  {p.VelocidadMs * 3.6:0} km/h");
                }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Simulación detenida.");
        }
        Console.WriteLine($"Posiciones guardadas: {guardadas}. Véalas en Monitoreo > Monitoreo de personal.");
        return 0;
    }

    /// <summary>Recorrido en GPX 1.1 (horas en UTC), el formato que reproduce el emulador de Android.</summary>
    private static void EscribirGpx(string archivo, IReadOnlyList<PosicionGps> posiciones, string nombre)
    {
        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        new XDocument(new XElement(ns + "gpx",
            new XAttribute("version", "1.1"), new XAttribute("creator", "VisorDatosSIG"),
            new XElement(ns + "trk", new XElement(ns + "name", nombre), new XElement(ns + "trkseg",
                posiciones.Select(p => new XElement(ns + "trkpt", new XAttribute("lat", p.Lat), new XAttribute("lon", p.Lon),
                    new XElement(ns + "time", p.FechaHora.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))))))))
            .Save(archivo);
    }

    private static string Contrasena()
    {
        var variable = Environment.GetEnvironmentVariable("VISOR_PASSWORD");
        if (!string.IsNullOrEmpty(variable)) return variable;
        Console.Write("Contraseña: ");
        var texto = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (texto.Length > 0) texto.Length--; continue; }
            texto.Append(k.KeyChar);
        }
        Console.WriteLine();
        return texto.ToString();
    }
}
