using NetTopologySuite.Geometries;
using VisorDatosSIG.Application.Comun;

namespace VisorDatosSIG.Application.Migracion;

public enum ModoCarga { Reemplazar, Anexar }

public static class ModosCarga
{
    public static ModoCarga Parsear(string? valor) => valor?.ToLowerInvariant() switch
    {
        null or "reemplazar" => ModoCarga.Reemplazar,
        "anexar" => ModoCarga.Anexar,
        _ => throw new ErrorValidacion("Modo de migración no válido. Use 'reemplazar' o 'anexar'."),
    };
}

public enum NivelIncidencia { Info, Advertencia, Error }

public sealed record Incidencia(NivelIncidencia Nivel, string Codigo, string Mensaje, int? Fila = null, string? Campo = null, string? Valor = null);

/// <summary>
/// Acumula incidencias sin inundar la bitácora: guarda el detalle de las primeras
/// <see cref="MaximoDetallePorCodigo"/> por código y cuenta el total de cada uno.
/// </summary>
public sealed class RegistroIncidencias
{
    public const int MaximoDetallePorCodigo = 25;
    private readonly List<Incidencia> _detalle = [];
    private readonly Dictionary<string, (NivelIncidencia Nivel, int Total, string Ejemplo)> _resumen = new();

    public void Agregar(Incidencia i)
    {
        _resumen.TryGetValue(i.Codigo, out var r);
        r = (i.Nivel, r.Total + 1, r.Ejemplo ?? i.Mensaje);
        _resumen[i.Codigo] = r;
        if (r.Total <= MaximoDetallePorCodigo) _detalle.Add(i);
    }

    public void Agregar(NivelIncidencia nivel, string codigo, string mensaje, int? fila = null, string? campo = null, string? valor = null) =>
        Agregar(new Incidencia(nivel, codigo, mensaje, fila, campo, valor));

    public IReadOnlyList<Incidencia> Detalle => _detalle;
    public int Errores => _resumen.Values.Where(r => r.Nivel == NivelIncidencia.Error).Sum(r => r.Total);
    public int Advertencias => _resumen.Values.Where(r => r.Nivel == NivelIncidencia.Advertencia).Sum(r => r.Total);
    public bool HayBloqueantes => _detalle.Any(i => i.Codigo.StartsWith("BLOQ_", StringComparison.Ordinal));

    /// <summary>Una línea por código con el total de casos (lo que no entra en el detalle queda contado).</summary>
    public IEnumerable<Incidencia> Resumen() => _resumen.Select(kv => new Incidencia(kv.Value.Nivel, kv.Key,
        kv.Value.Total > MaximoDetallePorCodigo
            ? $"{kv.Value.Total} casos (detalle de los primeros {MaximoDetallePorCodigo}). Ej.: {kv.Value.Ejemplo}"
            : $"{kv.Value.Total} caso(s). Ej.: {kv.Value.Ejemplo}"));

    public IEnumerable<Incidencia> ParaBitacora() => Resumen().Concat(_detalle);
}

/// <summary>Archivos asociados a un conjunto Shapefile (mismo nombre base, extensiones .shp/.shx/.dbf/.prj/.cpg).</summary>
public sealed record ArchivosShapefile(string Nombre, string Carpeta, string? Shp, string? Shx, string? Dbf, string? Prj, string? Cpg)
{
    public static ArchivosShapefile Desde(string rutaShp)
    {
        var carpeta = Path.GetDirectoryName(Path.GetFullPath(rutaShp))!;
        var nombre = Path.GetFileNameWithoutExtension(rutaShp);
        string? Buscar(string ext) => Directory.EnumerateFiles(carpeta, nombre + ".*")
            .FirstOrDefault(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(Path.GetFileNameWithoutExtension(f), nombre, StringComparison.OrdinalIgnoreCase));
        return new ArchivosShapefile(nombre, carpeta, Buscar(".shp"), Buscar(".shx"), Buscar(".dbf"), Buscar(".prj"), Buscar(".cpg"));
    }

    public IEnumerable<string> Faltantes(bool exigirPrj)
    {
        if (Shp is null) yield return ".shp";
        if (Shx is null) yield return ".shx";
        if (Dbf is null) yield return ".dbf";
        if (exigirPrj && Prj is null) yield return ".prj";
    }
}

public sealed record CampoDbf(string Nombre, string Tipo, int Longitud, int Decimales);

public sealed record InfoShapefile(
    string TipoGeometria,
    bool TieneZ,
    int Registros,
    IReadOnlyList<CampoDbf> Campos,
    Extension? Extension,
    string? ContenidoPrj,
    string Codificacion);

/// <summary>Registro leído del Shapefile: FID (base 0, como ArcGIS), geometría y atributos DBF.</summary>
public sealed record RegistroShapefile(int Fid, Geometry? Geometria, IReadOnlyDictionary<string, object?> Atributos);

/// <summary>Fila lista para insertar: valores por columna destino + geometría SRID 4326.</summary>
public sealed class FilaDestino
{
    public required int Fid { get; init; }
    public required Dictionary<string, object?> Valores { get; init; }
    public required Geometry Geometria { get; init; }
    public required string ClaveNatural { get; init; }
}

public interface ILectorShapefile
{
    InfoShapefile Inspeccionar(ArchivosShapefile archivos);
    IEnumerable<RegistroShapefile> Leer(ArchivosShapefile archivos, CancellationToken ct = default);
}

public sealed record ConteosCarga(int Leidos, int Insertados, int Omitidos, int Fallidos, int Reparados, int Advertencias);

public sealed record ResultadoVinculos(int TotalCodigos, int CodigosConLote, int TotalLotes, int LotesConManzana);

public interface IMigracionTransaccion : IAsyncDisposable
{
    /// <summary>Borra de forma segura (respetando claves foráneas) las tablas que se van a reemplazar.</summary>
    Task PrepararReemplazoAsync(IReadOnlyCollection<string> capas, CancellationToken ct);
    Task<HashSet<string>> ObtenerClavesExistentesAsync(string capa, CancellationToken ct);
    Task InsertarAsync(DefinicionCapaMigracion definicion, IReadOnlyList<FilaDestino> filas, CancellationToken ct);
    Task<int> RepararGeometriasInvalidasAsync(CancellationToken ct);
    Task<ResultadoVinculos> ActualizarVinculosAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<string, int>> ContarAsync(CancellationToken ct);
    Task ConfirmarAsync(CancellationToken ct);
    Task RevertirAsync();
}

public sealed record EntradaBitacoraMigracion(string Usuario, string Equipo, string Capa, string Archivo, string TablaDestino, ModoCarga Modo);

public sealed record MigracionHistorial(
    int IdMigracion, DateTime FechaInicio, DateTime? FechaFin, string Usuario, string? Equipo, string Capa, string Archivo,
    string TablaDestino, string Modo, string Estado, int RegistrosLeidos, int Insertados, int Omitidos, int Fallidos,
    int Reparados, int Advertencias, int? DuracionMs, string? Mensaje);

public sealed record DetalleMigracion(long IdDetalle, string Nivel, int? Fila, string? Campo, string? Valor, string Mensaje);

public interface IMigracionRepositorio
{
    Task<string?> ProbarConexionAsync(CancellationToken ct);
    /// <summary>Marca EnCurso de hace más de dos horas como Interrumpida al comenzar otra migración.</summary>
    Task<int> MarcarInterrumpidasAsync(CancellationToken ct);
    Task<int> IniciarBitacoraAsync(EntradaBitacoraMigracion entrada, CancellationToken ct);
    Task FinalizarBitacoraAsync(int idMigracion, string estado, ConteosCarga conteos, int duracionMs, string? mensaje, CancellationToken ct);
    Task RegistrarIncidenciasAsync(int idMigracion, IEnumerable<Incidencia> incidencias, CancellationToken ct);
    Task<IMigracionTransaccion> IniciarTransaccionAsync(CancellationToken ct);
    Task<string> ReconstruirIndicesEspacialesAsync(CancellationToken ct);
    Task<Pagina<MigracionHistorial>> ListarHistorialAsync(int pagina, int tamano, CancellationToken ct);
    Task<(MigracionHistorial? Cabecera, IReadOnlyList<DetalleMigracion> Detalle)> ObtenerAsync(int idMigracion, CancellationToken ct);
    Task<DateTime?> UltimaActualizacionAsync(CancellationToken ct);
}
