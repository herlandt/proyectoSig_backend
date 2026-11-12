using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using VisorDatosSIG.Api.Infraestructura;
using VisorDatosSIG.Application.Capas;
using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Consultas;
using VisorDatosSIG.Application.Migracion;
using VisorDatosSIG.Application.Seguridad;
using VisorDatosSIG.Infrastructure.Geo;

namespace VisorDatosSIG.Api.Controllers;

[ApiController]
[Route("api/capas")]
public sealed class CapasController(ConsultaServicio consultas, IMigracionRepositorio migraciones, IMemoryCache cache) : ControllerBase
{
    /// <summary>Catálogo de capas: estilos, campos consultables, conteo y extensión (y la extensión general).</summary>
    [HttpGet]
    public async Task<IActionResult> Catalogo(CancellationToken ct)
    {
        var datos = await cache.GetOrCreateAsync("catalogo-capas", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            var resumen = (await consultas.ResumenAsync(ct)).ToDictionary(r => r.Id);
            var ultima = await migraciones.UltimaActualizacionAsync(ct);
            var conteos = await consultas.ConteoPorEstadoAsync(ct);
            var conteoPorEstado = CatalogoCapas.EstadosServicio.ToDictionary(
                e => e.Valor, e => conteos.GetValueOrDefault(e.Valor));
            Extension? general = null;
            foreach (var r in resumen.Values.Where(r => r.Extension is not null))
                general = general is null ? r.Extension : general.Unir(r.Extension!);

            return new
            {
                ultimaActualizacion = ultima,
                extensionGeneral = general,
                estados = CatalogoCapas.EstadosServicio,
                conteoPorEstado,
                capas = CatalogoCapas.Todas.Select(c => new
                {
                    c.Id, c.Nombre, c.NombreSingular, c.TipoGeometria, c.Color, c.ColorRelleno, c.OpacidadRelleno,
                    c.Orden, c.ZoomMinimo, c.VisiblePorDefecto, c.CampoId,
                    registros = resumen.TryGetValue(c.Id, out var r) ? r.Registros : 0,
                    extension = resumen.TryGetValue(c.Id, out var r2) ? r2.Extension : null,
                    campos = c.Campos.Select(f => new
                    {
                        f.Clave, f.Etiqueta, tipo = f.Tipo.ToString(), f.Buscable, f.EnTabla, f.Ordenable,
                        operadores = Operadores.PorTipo[f.Tipo],
                    }),
                }),
            };
        });
        return Ok(datos);
    }

    /// <summary>FeatureCollection filtrada por la extensión visible (bbox) y el zoom.</summary>
    [HttpGet("{capa}/geojson")]
    public async Task<IActionResult> GeoJson(string capa, [FromQuery] string? bbox, [FromQuery] int zoom, CancellationToken ct)
    {
        var r = await consultas.PorExtensionAsync(capa, bbox, zoom, ct);
        return new JsonEscritoResultado(w => GeoJsonEscritor.EscribirColeccion(w, r.Entidades, m =>
        {
            m.WriteNumber("total", r.Entidades.Count);
            m.WriteBoolean("truncado", r.Truncado);
            m.WriteBoolean("fueraDeZoom", r.FueraDeZoom);
            m.WriteNumber("zoomMinimo", r.ZoomMinimo);
        }), "application/geo+json");
    }

    /// <summary>
    /// Capa «Dimensiones y superficie»: largo de cada lado y área de manzanas (zoom ≥ 17) y lotes (zoom ≥ 18),
    /// como puntos con el texto y el ángulo para rotularlos.
    /// </summary>
    [HttpGet("{capa}/dimensiones")]
    public async Task<IActionResult> Dimensiones(string capa, [FromQuery] string? bbox, [FromQuery] int zoom, CancellationToken ct)
    {
        int zoomMinimo = capa switch
        {
            CatalogoCapas.Manzanas => 17,
            CatalogoCapas.Lotes => 18,
            _ => throw new ErrorValidacion("Las dimensiones solo se calculan para manzanas y lotes.", "capa"),
        };
        var etiquetas = new List<EtiquetaDimension>();
        if (zoom >= zoomMinimo)
        {
            var r = await consultas.PorExtensionAsync(capa, bbox, zoom, ct);
            foreach (var e in r.Entidades.Where(e => e.Geometria is not null))
                etiquetas.AddRange(global::VisorDatosSIG.Application.Capas.Dimensiones.Calcular(e.Geometria!));
        }
        return Ok(new
        {
            type = "FeatureCollection", zoomMinimo, fueraDeZoom = zoom < zoomMinimo,
            features = etiquetas.Select(x => new
            {
                type = "Feature",
                geometry = new { type = "Point", coordinates = new[] { Math.Round(x.Lon, 7), Math.Round(x.Lat, 7) } },
                properties = new { x.Tipo, x.Texto, x.Angulo },
            }),
        });
    }

    /// <summary>Atributos completos y geometría de una entidad.</summary>
    [HttpGet("{capa}/{id:int}")]
    public async Task<IActionResult> Entidad(string capa, int id, CancellationToken ct)
    {
        var e = await consultas.PorIdAsync(capa, id, ct);
        if (e is null) return Problem(statusCode: 404, title: "No encontrado", detail: "La entidad solicitada no existe.");
        return new JsonEscritoResultado(w => GeoJsonEscritor.EscribirFeature(w, e), "application/geo+json");
    }
}

[ApiController]
[Route("api/identificar")]
public sealed class IdentificarController(ConsultaServicio consultas) : ControllerBase
{
    /// <summary>Entidades bajo el clic/toque, agrupadas por capa (RF-CON-01/02).</summary>
    [HttpGet]
    public async Task<IActionResult> Identificar(double lon, double lat, int zoom, string? capas, CancellationToken ct)
    {
        var lista = capas?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var grupos = await consultas.IdentificarAsync(lon, lat, zoom, lista, ct);
        return new JsonEscritoResultado(w => EscribirGrupos(w, grupos));
    }

    internal static void EscribirGrupos(Utf8JsonWriter w, IReadOnlyList<GrupoResultados> grupos)
    {
        w.WriteStartObject();
        w.WriteStartArray("grupos");
        foreach (var g in grupos)
        {
            w.WriteStartObject();
            w.WriteString("capa", g.Capa);
            w.WriteString("nombre", g.Nombre);
            w.WriteBoolean("hayMas", g.HayMas);
            w.WriteBoolean("limiteAlcanzado", g.LimiteAlcanzado);
            w.WritePropertyName("entidades");
            GeoJsonEscritor.EscribirColeccion(w, g.Entidades);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}

[ApiController]
[Route("api/busqueda")]
public sealed class BusquedaController(ConsultaServicio consultas, IBitacoraRepositorio bitacora) : ControllerBase
{
    private static readonly JsonSerializerOptions OpcionesJson = new(JsonSerializerDefaults.Web);
    private static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("es-BO");

    /// <summary>Consulta por capa con filtros combinados, orden y paginación; incluye la extensión combinada.</summary>
    [HttpGet]
    public async Task<IActionResult> Buscar([FromQuery] string capa, [FromQuery] string? filtros, [FromQuery] string? orden,
        [FromQuery] bool desc = false, [FromQuery] int pagina = 1, [FromQuery] int tamano = 20, [FromQuery] bool geometria = false,
        [FromQuery] string? textoGeneral = null,
        CancellationToken ct = default)
    {
        var solicitud = new SolicitudBusqueda(capa, LeerFiltros(filtros), orden, desc, pagina, tamano, geometria, TextoGeneral: textoGeneral);
        var r = await consultas.BuscarAsync(solicitud, ct);
        return new JsonEscritoResultado(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("total", r.Pagina.Total);
            w.WriteNumber("pagina", r.Pagina.NumeroPagina);
            w.WriteNumber("tamano", r.Pagina.Tamano);
            w.WriteNumber("totalPaginas", r.Pagina.TotalPaginas);
            w.WritePropertyName("extension");
            JsonSerializer.Serialize(w, r.Extension, OpcionesJson);
            w.WritePropertyName("resultados");
            GeoJsonEscritor.EscribirColeccion(w, r.Pagina.Elementos);
            w.WriteEndObject();
        });
    }

    /// <summary>Búsqueda rápida por código fijo, titular, lote, manzana o vía (parcial, sin distinguir mayúsculas).</summary>
    [HttpGet("general")]
    public async Task<IActionResult> General([FromQuery] string? texto, CancellationToken ct)
    {
        var grupos = await consultas.BuscarGeneralAsync(texto, ct);
        return new JsonEscritoResultado(w => IdentificarController.EscribirGrupos(w, grupos));
    }

    /// <summary>Exporta a CSV los resultados alfanuméricos filtrados (RF-CON-10).</summary>
    [HttpGet("csv")]
    public async Task<IActionResult> Csv([FromQuery] string capa, [FromQuery] string? filtros, [FromQuery] string? orden,
        [FromQuery] bool desc = false, [FromQuery] string? textoGeneral = null, CancellationToken ct = default)
    {
        var definicion = CatalogoCapas.Obtener(capa);
        var filas = await consultas.ExportarAsync(new SolicitudBusqueda(capa, LeerFiltros(filtros), orden, desc, TextoGeneral: textoGeneral), ct);

        var campos = definicion.Campos.ToList();
        bool conEstado = definicion.Id == CatalogoCapas.CodigosFijos;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', campos.Select(c => Celda(c.Etiqueta)).Concat(conEstado ? ["Descripción del estado"] : [])));
        foreach (var e in filas)
        {
            var valores = campos.Select(c => Celda(e.Atributos.GetValueOrDefault(c.Clave))).ToList();
            if (conEstado) valores.Add(Celda(e.Atributos.GetValueOrDefault("EstadoDescripcion")));
            sb.AppendLine(string.Join(';', valores));
        }
        await bitacora.RegistrarAccesoAsync(User.Id(), User.Login(), EventosBitacora.ExportacionCsv, true,
            this.Actor().Ip, $"{definicion.Nombre}: {filas.Count} filas", ct);

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"{definicion.Id}_{DateTime.Now:yyyyMMdd_HHmm}.csv");
    }

    private static IReadOnlyList<FiltroConsulta> LeerFiltros(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var filtros = JsonSerializer.Deserialize<List<FiltroConsulta>>(json, OpcionesJson) ?? [];
            if (filtros.Any(f => f is null))
                throw new ErrorValidacion("La lista de filtros no puede contener elementos nulos.", "filtros");
            return filtros;
        }
        catch (JsonException) { throw new ErrorValidacion("El parámetro 'filtros' no tiene un formato válido.", "filtros"); }
    }

    /// <summary>Celda CSV escapada; evita la inyección de fórmulas en Excel.</summary>
    private static string Celda(object? valor)
    {
        string texto = valor switch
        {
            null => "",
            DateTime d => d.ToString("yyyy-MM-dd HH:mm", Cultura),
            double d => d.ToString(Cultura),
            decimal m => m.ToString(Cultura),
            IFormattable f => f.ToString(null, Cultura),
            _ => valor.ToString() ?? "",
        };
        if (valor is string && texto.Length > 0 && "=+-@\t\r".Contains(texto[0])) texto = "'" + texto;
        return texto.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{texto.Replace("\"", "\"\"")}\"" : texto;
    }
}
