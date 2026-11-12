using System.Globalization;
using NetTopologySuite.Geometries;
using VisorDatosSIG.Application.Capas;
using VisorDatosSIG.Application.Comun;

namespace VisorDatosSIG.Application.Consultas;

/// <summary>Entidad de una capa: identificador, título legible, atributos y geometría (SRID 4326).</summary>
public sealed class EntidadGeo
{
    public required string Capa { get; init; }
    public required int Id { get; init; }
    public string Titulo { get; init; } = "";
    public Dictionary<string, object?> Atributos { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Geometry? Geometria { get; set; }
}

public sealed record ResultadoExtension(IReadOnlyList<EntidadGeo> Entidades, bool Truncado, bool FueraDeZoom, int ZoomMinimo);

public sealed record GrupoResultados(string Capa, string Nombre, IReadOnlyList<EntidadGeo> Entidades,
    bool HayMas = false, bool LimiteAlcanzado = false);

public sealed record ResumenCapa(string Id, int Registros, Extension? Extension);

public sealed record FiltroConsulta(string Campo, string Operador, string? Valor);

public sealed record SolicitudBusqueda(
    string Capa,
    IReadOnlyList<FiltroConsulta> Filtros,
    string? Orden = null,
    bool Descendente = false,
    int Pagina = 1,
    int Tamano = 20,
    bool IncluirGeometria = false,
    string? TextoGeneral = null);

public sealed record ResultadoBusqueda(Pagina<EntidadGeo> Pagina, Extension? Extension);

public static class Operadores
{
    public const string Contiene = "contiene";
    public const string Empieza = "empieza";
    public const string Igual = "igual";
    public const string Distinto = "distinto";
    public const string Mayor = "mayor";
    public const string Menor = "menor";
    public const string MayorIgual = "mayorigual";
    public const string MenorIgual = "menorigual";

    public static readonly IReadOnlyDictionary<TipoCampo, string[]> PorTipo = new Dictionary<TipoCampo, string[]>
    {
        [TipoCampo.Texto] = [Contiene, Empieza, Igual, Distinto],
        [TipoCampo.Numero] = [Igual, Distinto, Mayor, Menor, MayorIgual, MenorIgual, Contiene],
        [TipoCampo.Decimal] = [Igual, Mayor, Menor, MayorIgual, MenorIgual],
        [TipoCampo.Estado] = [Igual, Distinto],
        [TipoCampo.Fecha] = [],
    };
}

/// <summary>Acceso a datos de las capas (implementado con SQL parametrizado en Infrastructure).</summary>
public interface IConsultaCapasRepositorio
{
    Task<IReadOnlyList<ResumenCapa>> ObtenerResumenAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<int, int>> ObtenerConteoPorEstadoAsync(CancellationToken ct);
    Task<(IReadOnlyList<EntidadGeo> Entidades, bool Truncado)> ObtenerPorExtensionAsync(
        CapaDefinicion capa, Envelope bbox, double toleranciaSimplificacion, int limite, CancellationToken ct);
    Task<EntidadGeo?> ObtenerPorIdAsync(CapaDefinicion capa, int id, CancellationToken ct);
    Task<IReadOnlyList<EntidadGeo>> IdentificarAsync(
        CapaDefinicion capa, double lon, double lat, double tolerancia, int limite, CancellationToken ct);
    Task<ResultadoBusqueda> BuscarAsync(CapaDefinicion capa, SolicitudBusqueda solicitud, CancellationToken ct);
    Task<IReadOnlyList<EntidadGeo>> ExportarAsync(CapaDefinicion capa, SolicitudBusqueda solicitud, int maximo, CancellationToken ct);
    Task<IReadOnlyList<EntidadGeo>> BuscarInmueblesAsync(string texto, CancellationToken ct);
    Task<IReadOnlyList<EntidadGeo>> BuscarTextoAsync(CapaDefinicion capa, string texto, int limite, CancellationToken ct);
}

/// <summary>Casos de uso del visor: valida todas las entradas antes de llegar a SQL.</summary>
public sealed class ConsultaServicio(IConsultaCapasRepositorio repositorio)
{
    public const int LimiteEntidadesPorExtension = 16000;   // cubre todos los lotes; la carga sigue acotada por bbox y zoom
    public const int MaximoExportacion = 50000;
    public const int MaximoFiltros = 5;
    private const double MetrosPorGrado = 111_320d;

    public Task<IReadOnlyList<ResumenCapa>> ResumenAsync(CancellationToken ct) => repositorio.ObtenerResumenAsync(ct);

    public Task<IReadOnlyDictionary<int, int>> ConteoPorEstadoAsync(CancellationToken ct) => repositorio.ObtenerConteoPorEstadoAsync(ct);

    public async Task<ResultadoExtension> PorExtensionAsync(string capaId, string? bbox, int zoom, CancellationToken ct)
    {
        var capa = CatalogoCapas.Obtener(capaId);
        ValidarZoom(zoom);
        var envelope = ParsearBbox(bbox);
        if (zoom < capa.ZoomMinimo)
            return new ResultadoExtension([], false, true, capa.ZoomMinimo);

        // Simplificación según la resolución del zoom (medio píxel) para polígonos y líneas.
        double tolerancia = capa.TipoGeometria == "Punto" || zoom >= 18
            ? 0
            : MetrosPorPixel(zoom, envelope.Centre.Y) * 0.5 / MetrosPorGrado;

        var (entidades, truncado) = await repositorio.ObtenerPorExtensionAsync(
            capa, envelope, tolerancia, LimiteEntidadesPorExtension, ct);
        return new ResultadoExtension(entidades, truncado, false, capa.ZoomMinimo);
    }

    public Task<EntidadGeo?> PorIdAsync(string capaId, int id, CancellationToken ct)
    {
        var capa = CatalogoCapas.Obtener(capaId);
        if (id <= 0) throw new ErrorValidacion("El identificador debe ser un entero positivo.", "id");
        return repositorio.ObtenerPorIdAsync(capa, id, ct);
    }

    public async Task<IReadOnlyList<GrupoResultados>> IdentificarAsync(
        double lon, double lat, int zoom, IEnumerable<string>? capas, CancellationToken ct)
    {
        ValidarCoordenadas(lon, lat);
        ValidarZoom(zoom);
        var seleccion = (capas ?? CatalogoCapas.Todas.Select(c => c.Id))
            .Select(CatalogoCapas.Obtener).DistinctBy(c => c.Id).OrderBy(c => c.Orden).ToList();

        // Tolerancia de 8 píxeles en pantalla, expresada en grados.
        double tolerancia = MetrosPorPixel(zoom, lat) * 8 / MetrosPorGrado;
        var grupos = new List<GrupoResultados>();
        // Orden de prioridad para el clic: primero lo puntual y lineal, luego polígonos.
        foreach (var capa in seleccion.OrderBy(PrioridadIdentificacion))
        {
            int limite = capa.TipoGeometria == "Polígono" ? 3 : 5;
            var entidades = await repositorio.IdentificarAsync(capa, lon, lat, tolerancia, limite, ct);
            if (entidades.Count > 0) grupos.Add(new GrupoResultados(capa.Id, capa.Nombre, entidades));
        }
        return grupos;
    }

    public Task<ResultadoBusqueda> BuscarAsync(SolicitudBusqueda solicitud, CancellationToken ct)
    {
        var (capa, normalizada) = Normalizar(solicitud, pagina: true);
        return repositorio.BuscarAsync(capa, normalizada, ct);
    }

    public async Task<IReadOnlyList<EntidadGeo>> ExportarAsync(SolicitudBusqueda solicitud, CancellationToken ct)
    {
        var (capa, normalizada) = Normalizar(solicitud, pagina: false);
        // Una fila adicional permite detectar el límite sin devolver un CSV incompleto.
        var filas = await repositorio.ExportarAsync(capa, normalizada with { IncluirGeometria = false }, MaximoExportacion + 1, ct);
        if (filas.Count > MaximoExportacion)
            throw new ErrorValidacion("La consulta supera el límite de 50.000 registros para exportar. Agregue filtros y vuelva a intentarlo.", "filtros");
        return filas;
    }

    /// <summary>Búsqueda rápida en todas las capas: código fijo/titular (sp_BuscarInmueble), lote, manzana y vía.</summary>
    public async Task<IReadOnlyList<GrupoResultados>> BuscarGeneralAsync(string? texto, CancellationToken ct)
    {
        texto = texto?.Trim();
        if (string.IsNullOrEmpty(texto) || texto.Length < 2)
            throw new ErrorValidacion("Escriba al menos 2 caracteres para buscar.", "texto");
        if (texto.Length > 100) throw new ErrorValidacion("La búsqueda admite hasta 100 caracteres.", "texto");

        var grupos = new List<GrupoResultados>();
        var inmuebles = await repositorio.BuscarInmueblesAsync(texto, ct);
        if (inmuebles.Count > 0)
            grupos.Add(new GrupoResultados(CatalogoCapas.CodigosFijos, "Códigos fijos", inmuebles, LimiteAlcanzado: inmuebles.Count >= 100));
        foreach (var id in new[] { CatalogoCapas.Lotes, CatalogoCapas.Manzanas, CatalogoCapas.Vias })
        {
            var capa = CatalogoCapas.Obtener(id);
            var encontrados = await repositorio.BuscarTextoAsync(capa, texto, 26, ct);
            if (encontrados.Count > 0)
                grupos.Add(new GrupoResultados(capa.Id, capa.Nombre, encontrados.Take(25).ToList(), HayMas: encontrados.Count > 25));
        }
        return grupos;
    }

    private static (CapaDefinicion, SolicitudBusqueda) Normalizar(SolicitudBusqueda s, bool pagina)
    {
        var capa = CatalogoCapas.Obtener(s.Capa);
        var filtros = s.Filtros ?? [];
        if (filtros.Count > MaximoFiltros)
            throw new ErrorValidacion($"Se admiten hasta {MaximoFiltros} filtros combinados.", "filtros");

        var limpios = new List<FiltroConsulta>();
        foreach (var f in filtros)
        {
            if (f is null) throw new ErrorValidacion("La lista de filtros no puede contener elementos nulos.", "filtros");
            var campo = capa.Campo(f.Campo)
                ?? throw new ErrorValidacion($"El campo '{f.Campo}' no existe en la capa {capa.Nombre}.", "campo");
            if (!campo.Buscable)
                throw new ErrorValidacion($"El campo '{campo.Etiqueta}' no admite filtros.", "campo");
            var operador = (f.Operador ?? "").Trim().ToLowerInvariant();
            if (!Operadores.PorTipo[campo.Tipo].Contains(operador))
                throw new ErrorValidacion($"El operador '{f.Operador}' no aplica al campo '{campo.Etiqueta}'.", "operador");
            var valor = f.Valor?.Trim() ?? "";
            if (valor.Length == 0)
                throw new ErrorValidacion($"Indique un valor para el campo '{campo.Etiqueta}'.", "valor");
            if (valor.Length > 100)
                throw new ErrorValidacion("Cada valor de filtro admite hasta 100 caracteres.", "valor");
            if (campo.Tipo is TipoCampo.Numero or TipoCampo.Estado && operador != Operadores.Contiene
                && !long.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                throw new ErrorValidacion($"El campo '{campo.Etiqueta}' espera un número entero.", "valor");
            if (campo.Tipo == TipoCampo.Decimal
                && !double.TryParse(valor.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new ErrorValidacion($"El campo '{campo.Etiqueta}' espera un número.", "valor");
            limpios.Add(new FiltroConsulta(campo.Clave, operador, valor));
        }

        string? orden = null;
        if (!string.IsNullOrWhiteSpace(s.Orden))
        {
            var campoOrden = capa.Campo(s.Orden);
            if (campoOrden is null || !campoOrden.Ordenable)
                throw new ErrorValidacion($"No se puede ordenar por '{s.Orden}'.", "orden");
            orden = campoOrden.Clave;
        }

        if (pagina && s.Pagina is < 1 or > 100000)
            throw new ErrorValidacion("La página debe estar entre 1 y 100000.", "pagina");
        string? textoGeneral = string.IsNullOrWhiteSpace(s.TextoGeneral) ? null : s.TextoGeneral.Trim();
        if (textoGeneral is not null && textoGeneral.Length is < 2 or > 100)
            throw new ErrorValidacion("La búsqueda general admite entre 2 y 100 caracteres.", "textoGeneral");
        int numero = pagina ? s.Pagina : 1;
        int tamano = pagina ? Math.Clamp(s.Tamano, 5, 100) : MaximoExportacion;
        return (capa, s with { Capa = capa.Id, Filtros = limpios, Orden = orden, Pagina = numero, Tamano = tamano, TextoGeneral = textoGeneral });
    }

    public static Envelope ParsearBbox(string? bbox)
    {
        var partes = (bbox ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (partes.Length != 4 || !partes.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            throw new ErrorValidacion("El parámetro bbox debe tener el formato minLon,minLat,maxLon,maxLat.", "bbox");
        var v = partes.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (v[0] >= v[2] || v[1] >= v[3])
            throw new ErrorValidacion("El bbox es inválido: el mínimo debe ser menor que el máximo.", "bbox");
        ValidarCoordenadas(v[0], v[1]);
        ValidarCoordenadas(v[2], v[3]);
        return new Envelope(v[0], v[2], v[1], v[3]);
    }

    private static void ValidarCoordenadas(double lon, double lat)
    {
        if (double.IsNaN(lon) || double.IsNaN(lat) || lon is < -180 or > 180 || lat is < -90 or > 90)
            throw new ErrorValidacion("Las coordenadas deben ser WGS 84 (longitud -180..180, latitud -90..90).", "coordenadas");
    }

    private static void ValidarZoom(int zoom)
    {
        if (zoom is < 0 or > 22) throw new ErrorValidacion("El nivel de zoom debe estar entre 0 y 22.", "zoom");
    }

    private static int PrioridadIdentificacion(CapaDefinicion c) => c.TipoGeometria switch
    {
        "Punto" => 0,
        "Línea" => 1,
        _ => c.Id == CatalogoCapas.Lotes ? 2 : 3   // el lote (más pequeño) antes que la manzana
    };

    private static double MetrosPorPixel(int zoom, double latitud) =>
        156_543.033_92 * Math.Cos(latitud * Math.PI / 180) / Math.Pow(2, zoom);
}
