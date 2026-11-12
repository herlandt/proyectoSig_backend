namespace VisorDatosSIG.Application.Capas;

public enum TipoCampo { Texto, Numero, Decimal, Estado, Fecha }

/// <summary>Campo publicado por una capa. Solo los campos del catálogo se pueden consultar u ordenar.</summary>
public sealed record CampoCapa(
    string Clave,
    string Etiqueta,
    TipoCampo Tipo,
    bool Buscable = true,
    bool EnTabla = true,
    bool Ordenable = true);

public sealed record CapaDefinicion(
    string Id,
    string Nombre,
    string NombreSingular,
    string TablaSql,
    string TipoGeometria,
    string Color,
    string ColorRelleno,
    double OpacidadRelleno,
    int Orden,
    int ZoomMinimo,
    bool VisiblePorDefecto,
    string CampoId,
    IReadOnlyList<CampoCapa> Campos)
{
    public CampoCapa? Campo(string clave) =>
        Campos.FirstOrDefault(c => string.Equals(c.Clave, clave, StringComparison.OrdinalIgnoreCase));
}

public sealed record SimboloEstado(int Valor, string Nombre, string Color, string Forma);

/// <summary>
/// Catálogo de las cuatro capas temáticas: nombres, estilos (mismos colores en selector,
/// leyenda, mapa y tabla), zoom mínimo de carga y campos consultables.
/// </summary>
public static class CatalogoCapas
{
    public const string Manzanas = "manzanas";
    public const string Lotes = "lotes";
    public const string CodigosFijos = "codigosfijos";
    public const string Vias = "vias";

    public static readonly IReadOnlyList<CapaDefinicion> Todas =
    [
        new(Manzanas, "Manzanas", "Manzana", "dbo.Manzanas", "Polígono",
            "#1F5FA8", "#1F5FA8", 0.04, 1, 11, true, "IdManzana",
            [
                new("IdManzana", "Id", TipoCampo.Numero),
                new("UV", "Unidad vecinal", TipoCampo.Texto),
                new("MZA", "Manzana", TipoCampo.Texto),
                new("UV_MZA", "UV y manzana", TipoCampo.Texto),
                new("CantidadLotes", "Cantidad de lotes", TipoCampo.Numero, Buscable: false, Ordenable: false),
                new("Superficie", "Superficie (m²)", TipoCampo.Decimal, Buscable: false, Ordenable: false),
            ]),
        new(Lotes, "Lotes", "Lote", "dbo.Lotes", "Polígono",
            "#B7791F", "#F6C344", 0.12, 2, 15, true, "IdLote",
            [
                new("IdLote", "Id", TipoCampo.Numero),
                new("NroLote", "Código de lote", TipoCampo.Texto),
                new("UV", "Unidad vecinal", TipoCampo.Texto),
                new("MZA", "Manzana", TipoCampo.Texto),
                new("IdManzana", "Id manzana", TipoCampo.Numero, EnTabla: false),
                new("CantidadCodigos", "Códigos fijos", TipoCampo.Numero, Buscable: false, Ordenable: false),
                new("Superficie", "Superficie (m²)", TipoCampo.Decimal, Buscable: false, Ordenable: false),
            ]),
        new(CodigosFijos, "Códigos fijos", "Código fijo", "dbo.CodigosFijos", "Punto",
            "#198754", "#198754", 0.9, 3, 14, true, "IdCodigo",
            [
                new("IdCodigo", "Id", TipoCampo.Numero, EnTabla: false),
                new("CodFijo", "Código fijo", TipoCampo.Numero),
                new("CodF_SIG", "Código SIG", TipoCampo.Texto),
                new("CodF_SQL", "Código SQL", TipoCampo.Numero, EnTabla: false),
                new("Nombre", "Usuario / titular", TipoCampo.Texto),
                new("Estado", "Estado del servicio", TipoCampo.Estado),
                new("FechaCambioEstado", "Fecha de cambio de estado", TipoCampo.Fecha, Buscable: false, EnTabla: false),
                new("NroLote", "Lote", TipoCampo.Texto),
                new("UV", "Unidad vecinal", TipoCampo.Texto),
                new("MZA", "Manzana", TipoCampo.Texto),
                new("Longitud", "Longitud", TipoCampo.Decimal, Buscable: false, EnTabla: false),
                new("Latitud", "Latitud", TipoCampo.Decimal, Buscable: false, EnTabla: false),
            ]),
        new(Vias, "Vías", "Vía", "dbo.Vias", "Línea",
            "#5C6670", "#5C6670", 0, 4, 11, true, "IdVia",
            [
                new("IdVia", "Id", TipoCampo.Numero, EnTabla: false),
                new("Nombre", "Nombre de la vía", TipoCampo.Texto),
                new("TipoVia", "Tipo de vía", TipoCampo.Texto),
                new("OSMID", "Id OSM", TipoCampo.Texto, EnTabla: false),
                new("OBJECTID", "OBJECTID", TipoCampo.Numero, EnTabla: false),
                new("LongitudMetros", "Longitud (m)", TipoCampo.Decimal, Buscable: false, Ordenable: false),
            ]),
    ];

    /// <summary>Simbología por estado del servicio (colores del sistema GeoConsulta + forma, para no depender solo del color).</summary>
    public static readonly IReadOnlyList<SimboloEstado> EstadosServicio =
    [
        new(1, "Normal", "#198754", "circulo"),
        new(2, "Para Corte", "#FD7E14", "triangulo"),
        new(3, "Cortado", "#DC3545", "cuadrado"),
        new(4, "Baja Parcial", "#8D6E63", "rombo"),
        new(5, "Baja Total", "#212529", "cruz"),
    ];

    public static CapaDefinicion? Buscar(string? id) =>
        Todas.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public static CapaDefinicion Obtener(string? id) =>
        Buscar(id) ?? throw new Comun.ErrorValidacion($"La capa '{id}' no existe en el catálogo.", "capa");
}
