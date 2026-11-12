using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using NetTopologySuite.Geometries;
using VisorDatosSIG.Application.Capas;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Consultas;
using VisorDatosSIG.Domain;
using VisorDatosSIG.Infrastructure.Geo;

namespace VisorDatosSIG.Infrastructure.Datos;

public sealed class FabricaConexion(string cadenaConexion)
{
    public SqlConnection Crear() => new(cadenaConexion);
}

/// <summary>Consultas espaciales y alfanuméricas sobre SQL Server (siempre parametrizadas).</summary>
public sealed class ConsultaCapasRepositorio(FabricaConexion fabrica) : IConsultaCapasRepositorio
{
    private const int TiempoComando = 60;

    public async Task<IReadOnlyList<ResumenCapa>> ObtenerResumenAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT 'manzanas' AS Id, COUNT(*) AS Registros, geometry::EnvelopeAggregate(Geom).STAsBinary() AS Ext FROM dbo.Manzanas
            UNION ALL SELECT 'lotes', COUNT(*), geometry::EnvelopeAggregate(Geom).STAsBinary() FROM dbo.Lotes
            UNION ALL SELECT 'codigosfijos', COUNT(*), geometry::EnvelopeAggregate(Geom).STAsBinary() FROM dbo.CodigosFijos
            UNION ALL SELECT 'vias', COUNT(*), geometry::EnvelopeAggregate(Geom).STAsBinary() FROM dbo.Vias;
            """;
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<(string Id, int Registros, byte[]? Ext)>(new CommandDefinition(sql, cancellationToken: ct, commandTimeout: TiempoComando));
        return filas.Select(f => new ResumenCapa(f.Id, f.Registros,
            Extension.DesdeEnvelope(GeoJsonEscritor.DesdeWkb(f.Ext)?.EnvelopeInternal))).ToList();
    }

    public async Task<IReadOnlyDictionary<int, int>> ObtenerConteoPorEstadoAsync(CancellationToken ct)
    {
        const string sql = "SELECT Estado, COUNT(*) AS Total FROM dbo.CodigosFijos GROUP BY Estado;";
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync<(int Estado, int Total)>(new CommandDefinition(sql, cancellationToken: ct, commandTimeout: TiempoComando));
        return filas.ToDictionary(f => f.Estado, f => f.Total);
    }

    public async Task<(IReadOnlyList<EntidadGeo> Entidades, bool Truncado)> ObtenerPorExtensionAsync(
        CapaDefinicion capa, Envelope bbox, double toleranciaSimplificacion, int limite, CancellationToken ct)
    {
        var sc = SqlCatalogo.Obtener(capa);
        string geom = toleranciaSimplificacion > 0 ? $"{sc.Geom}.Reduce(@tol)" : sc.Geom;
        string sql = $"""
            DECLARE @bb geometry = geometry::STGeomFromText(@wkt, 4326);
            SELECT TOP (@lim) {sc.Id} AS __id{SqlCatalogo.Columnas(sc, sc.CamposMapa)}, {geom}.STAsBinary() AS __wkb
            FROM {sc.From}
            WHERE {sc.Geom}.Filter(@bb) = 1;
            """;
        var p = new DynamicParameters();
        p.Add("wkt", WktRectangulo(bbox), DbType.String, size: 400);
        p.Add("lim", limite + 1);
        p.Add("tol", toleranciaSimplificacion);
        var entidades = await ConsultarEntidadesAsync(capa, sql, p, ct);
        bool truncado = entidades.Count > limite;
        if (truncado) entidades.RemoveAt(entidades.Count - 1);
        entidades.RemoveAll(e => e.Geometria is null || e.Geometria.IsEmpty);
        return (entidades, truncado);
    }

    public async Task<EntidadGeo?> ObtenerPorIdAsync(CapaDefinicion capa, int id, CancellationToken ct)
    {
        var sc = SqlCatalogo.Obtener(capa);
        string sql = $"""
            SELECT {sc.Id} AS __id, {sc.Titulo} AS __titulo{SqlCatalogo.Columnas(sc, capa.Campos.Select(c => c.Clave))}, {sc.Geom}.STAsBinary() AS __wkb
            FROM {sc.From} WHERE {sc.Id} = @id;
            """;
        var lista = await ConsultarEntidadesAsync(capa, sql, new { id }, ct);
        return lista.FirstOrDefault();
    }

    public async Task<IReadOnlyList<EntidadGeo>> IdentificarAsync(
        CapaDefinicion capa, double lon, double lat, double tolerancia, int limite, CancellationToken ct)
    {
        var sc = SqlCatalogo.Obtener(capa);
        string orden = sc.EsPoligono ? $"{sc.Geom}.STDistance(@p), {sc.Geom}.STArea()" : $"{sc.Geom}.STDistance(@p)";
        string sql = $"""
            DECLARE @p geometry = geometry::Point(@lon, @lat, 4326);
            DECLARE @a geometry = @p.STBuffer(@tol);
            SELECT TOP (@lim) {sc.Id} AS __id, {sc.Titulo} AS __titulo{SqlCatalogo.Columnas(sc, capa.Campos.Select(c => c.Clave))}, {sc.Geom}.STAsBinary() AS __wkb
            FROM {sc.From}
            WHERE {sc.Geom}.Filter(@a) = 1 AND {sc.Geom}.STIntersects(@a) = 1
            ORDER BY {orden};
            """;
        return await ConsultarEntidadesAsync(capa, sql, new { lon, lat, tol = tolerancia, lim = limite }, ct);
    }

    public async Task<ResultadoBusqueda> BuscarAsync(CapaDefinicion capa, SolicitudBusqueda s, CancellationToken ct)
    {
        var sc = SqlCatalogo.Obtener(capa);
        var (where, p) = ConstruirFiltro(capa, sc, s.Filtros, s.TextoGeneral);
        p.Add("off", (s.Pagina - 1) * s.Tamano);
        p.Add("tam", s.Tamano);
        string geometria = s.IncluirGeometria ? $", {sc.Geom}.STAsBinary() AS __wkb" : "";
        string extension = s.Pagina == 1
            ? $"SELECT geometry::EnvelopeAggregate({sc.Geom}).STAsBinary() FROM {sc.From} WHERE {where};"
            : "SELECT CAST(NULL AS varbinary(max));";
        string sql = $"""
            SELECT COUNT(*) FROM {sc.From} WHERE {where};
            {extension}
            SELECT {sc.Id} AS __id, {sc.Titulo} AS __titulo{SqlCatalogo.Columnas(sc, capa.Campos.Select(c => c.Clave))}{geometria}
            FROM {sc.From} WHERE {where}
            ORDER BY {Orden(capa, sc, s)}
            OFFSET @off ROWS FETCH NEXT @tam ROWS ONLY;
            """;
        await using var cn = fabrica.Crear();
        await using var multi = await cn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct, commandTimeout: TiempoComando));
        int total = await multi.ReadSingleAsync<int>();
        var ext = await multi.ReadSingleOrDefaultAsync<byte[]?>();
        var filas = (await multi.ReadAsync()).Cast<IDictionary<string, object>>();
        var entidades = filas.Select(f => AEntidad(capa, f)).ToList();
        return new ResultadoBusqueda(new Pagina<EntidadGeo>(total, s.Pagina, s.Tamano, entidades),
            Extension.DesdeEnvelope(GeoJsonEscritor.DesdeWkb(ext)?.EnvelopeInternal));
    }

    public async Task<IReadOnlyList<EntidadGeo>> ExportarAsync(CapaDefinicion capa, SolicitudBusqueda s, int maximo, CancellationToken ct)
    {
        var sc = SqlCatalogo.Obtener(capa);
        var (where, p) = ConstruirFiltro(capa, sc, s.Filtros, s.TextoGeneral);
        p.Add("max", maximo);
        string sql = $"""
            SELECT TOP (@max) {sc.Id} AS __id, {sc.Titulo} AS __titulo{SqlCatalogo.Columnas(sc, capa.Campos.Select(c => c.Clave))}
            FROM {sc.From} WHERE {where}
            ORDER BY {Orden(capa, sc, s)};
            """;
        return await ConsultarEntidadesAsync(capa, sql, p, ct, tiempo: 300);
    }

    public async Task<IReadOnlyList<EntidadGeo>> BuscarInmueblesAsync(string texto, CancellationToken ct)
    {
        // Procedimiento oficial del diseño físico para la búsqueda crítica (código fijo o titular).
        await using var cn = fabrica.Crear();
        var p = new DynamicParameters();
        p.Add("Texto", texto, DbType.String, size: 100);
        var filas = await cn.QueryAsync(new CommandDefinition("dbo.sp_BuscarInmueble", p,
            commandType: CommandType.StoredProcedure, cancellationToken: ct, commandTimeout: TiempoComando));
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        return filas.Cast<IDictionary<string, object>>().Select(f =>
        {
            var lon = f["Longitud"] as double?;
            var lat = f["Latitud"] as double?;
            var e = new EntidadGeo
            {
                Capa = CatalogoCapas.CodigosFijos,
                Id = Convert.ToInt32(f["IdCodigo"], CultureInfo.InvariantCulture),
                Titulo = $"Código {f["CodFijo"]} · {f["Nombre"]}",
                Geometria = lon is null || lat is null ? null : factory.CreatePoint(new Coordinate(lon.Value, lat.Value)),
            };
            foreach (var k in new[] { "CodFijo", "Nombre", "Estado", "UV", "MZA", "NroLote" })
                e.Atributos[k] = f[k] is DBNull ? null : f[k];
            AgregarDerivados(e);
            return e;
        }).ToList();
    }

    public async Task<IReadOnlyList<EntidadGeo>> BuscarTextoAsync(CapaDefinicion capa, string texto, int limite, CancellationToken ct)
    {
        var sc = SqlCatalogo.Obtener(capa);
        var condicion = string.Join(" OR ", sc.CamposTextoRapido.Select(c => $"{c} COLLATE Latin1_General_CI_AI LIKE @t ESCAPE N'\\'"));
        string sql = $"""
            SELECT TOP (@lim) {sc.Id} AS __id, {sc.Titulo} AS __titulo{SqlCatalogo.Columnas(sc, capa.Campos.Select(c => c.Clave))}, {sc.Geom}.STAsBinary() AS __wkb
            FROM {sc.From} WHERE {condicion}
            ORDER BY {sc.Id};
            """;
        var p = new DynamicParameters();
        p.Add("t", $"%{EscaparLike(texto)}%", DbType.String, size: 210);
        p.Add("lim", limite);
        return await ConsultarEntidadesAsync(capa, sql, p, ct);
    }

    // ---------------------------------------------------------------- utilidades

    private async Task<List<EntidadGeo>> ConsultarEntidadesAsync(CapaDefinicion capa, string sql, object parametros,
        CancellationToken ct, int tiempo = TiempoComando)
    {
        await using var cn = fabrica.Crear();
        var filas = await cn.QueryAsync(new CommandDefinition(sql, parametros, cancellationToken: ct, commandTimeout: tiempo));
        return filas.Cast<IDictionary<string, object>>().Select(f => AEntidad(capa, f)).ToList();
    }

    private static EntidadGeo AEntidad(CapaDefinicion capa, IDictionary<string, object> fila)
    {
        var e = new EntidadGeo
        {
            Capa = capa.Id,
            Id = Convert.ToInt32(fila["__id"], CultureInfo.InvariantCulture),
            Titulo = fila.TryGetValue("__titulo", out var t) ? t as string ?? "" : "",
            Geometria = fila.TryGetValue("__wkb", out var wkb) ? GeoJsonEscritor.DesdeWkb(wkb as byte[]) : null,
        };
        foreach (var (clave, valor) in fila)
            if (!clave.StartsWith("__", StringComparison.Ordinal))
                e.Atributos[clave] = valor is DBNull ? null : valor;
        AgregarDerivados(e);
        return e;
    }

    private static void AgregarDerivados(EntidadGeo e)
    {
        if (e.Atributos.TryGetValue("Estado", out var estado) && estado is not null)
            e.Atributos["EstadoDescripcion"] = EstadoServicioExtensiones.Descripcion(Convert.ToInt32(estado, CultureInfo.InvariantCulture));
    }

    private static (string Where, DynamicParameters Parametros) ConstruirFiltro(
        CapaDefinicion capa, SqlCapa sc, IReadOnlyList<FiltroConsulta> filtros, string? textoGeneral = null)
    {
        var p = new DynamicParameters();
        var condiciones = new List<string> { $"{sc.Geom} IS NOT NULL" };
        if (textoGeneral is not null)
        {
            // Mismos campos y comparación que BuscarTextoAsync; los filtros adicionales se combinan con AND.
            condiciones.Add("(" + string.Join(" OR ", sc.CamposTextoRapido.Select(c => $"{c} COLLATE Latin1_General_CI_AI LIKE @textoGeneral ESCAPE N'\\'")) + ")");
            p.Add("textoGeneral", $"%{EscaparLike(textoGeneral)}%", DbType.String, size: 210);
        }
        for (int i = 0; i < filtros.Count; i++)
        {
            var f = filtros[i];
            var campo = capa.Campo(f.Campo)!;
            string expr = sc.Campos[campo.Clave], n = $"@f{i}";
            string valor = f.Valor!;
            if (campo.Tipo == TipoCampo.Texto)
            {
                string limpio = $"LTRIM(RTRIM({expr})) COLLATE Latin1_General_CI_AI";
                (string condicion, string parametro) = f.Operador switch
                {
                    Operadores.Contiene => ($"{expr} COLLATE Latin1_General_CI_AI LIKE {n} ESCAPE N'\\'", $"%{EscaparLike(valor)}%"),
                    Operadores.Empieza => ($"{limpio} LIKE {n} ESCAPE N'\\'", $"{EscaparLike(valor)}%"),
                    Operadores.Igual => ($"{limpio} = {n}", valor),
                    _ => ($"({expr} IS NULL OR {limpio} <> {n})", valor),
                };
                condiciones.Add(condicion);
                p.Add(n, parametro, DbType.String, size: 210);
            }
            else if (f.Operador == Operadores.Contiene)
            {
                condiciones.Add($"CONVERT(NVARCHAR(30), {expr}) LIKE {n} ESCAPE N'\\'");
                p.Add(n, $"%{EscaparLike(valor)}%", DbType.String, size: 210);
            }
            else
            {
                string op = f.Operador switch
                {
                    Operadores.Igual => "=", Operadores.Distinto => "<>", Operadores.Mayor => ">",
                    Operadores.Menor => "<", Operadores.MayorIgual => ">=", _ => "<=",
                };
                condiciones.Add(f.Operador == Operadores.Distinto ? $"({expr} IS NULL OR {expr} <> {n})" : $"{expr} {op} {n}");
                if (campo.Tipo == TipoCampo.Decimal)
                    p.Add(n, double.Parse(valor.Replace(',', '.'), CultureInfo.InvariantCulture), DbType.Double);
                else
                    p.Add(n, long.Parse(valor, CultureInfo.InvariantCulture), DbType.Int64);
            }
        }
        return (string.Join(" AND ", condiciones), p);
    }

    private static string Orden(CapaDefinicion capa, SqlCapa sc, SolicitudBusqueda s)
    {
        string direccion = s.Descendente ? "DESC" : "ASC";
        return s.Orden is { } clave && capa.Campo(clave) is { Ordenable: true } campo
            ? $"{sc.Campos[campo.Clave]} {direccion}, {sc.Id}"
            : $"{sc.Id} {direccion}";
    }

    internal static string EscaparLike(string texto) =>
        texto.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static string WktRectangulo(Envelope e) => string.Create(CultureInfo.InvariantCulture,
        $"POLYGON(({e.MinX} {e.MinY}, {e.MaxX} {e.MinY}, {e.MaxX} {e.MaxY}, {e.MinX} {e.MaxY}, {e.MinX} {e.MinY}))");
}
