using VisorDatosSIG.Application.Capas;

namespace VisorDatosSIG.Infrastructure.Datos;

/// <summary>
/// Traducción de cada campo del catálogo a una expresión SQL fija. Las consultas solo
/// usan estas expresiones (nunca texto del usuario) y los valores van como parámetros.
/// </summary>
internal sealed record SqlCapa(
    string From,
    string Id,
    string Geom,
    string Titulo,
    IReadOnlyDictionary<string, string> Campos,
    IReadOnlyList<string> CamposMapa,
    IReadOnlyList<string> CamposTextoRapido,
    bool EsPoligono);

internal static class SqlCatalogo
{
    // Superficie geodésica en m²: el geometry 4326 se pasa a geography; si el anillo quedó
    // invertido (objeto "más grande que un hemisferio") se reorienta.
    private static string Superficie(string g) =>
        $"ROUND((SELECT CASE WHEN gg.EnvelopeAngle() > 90 THEN gg.ReorientObject().STArea() ELSE gg.STArea() END " +
        $"FROM (SELECT geography::STGeomFromWKB({g}.MakeValid().STAsBinary(), 4326).MakeValid() AS gg) AS s_), 2)";

    private static readonly Dictionary<string, SqlCapa> Capas = new(StringComparer.OrdinalIgnoreCase)
    {
        [CatalogoCapas.Manzanas] = new(
            "dbo.Manzanas m", "m.IdManzana", "m.Geom",
            "COALESCE(NULLIF(LTRIM(RTRIM(m.UV_MZA)), N''), CONCAT(N'Manzana #', m.IdManzana))",
            new Dictionary<string, string>
            {
                ["IdManzana"] = "m.IdManzana",
                ["UV"] = "m.UV",
                ["MZA"] = "m.MZA",
                ["UV_MZA"] = "m.UV_MZA",
                ["CantidadLotes"] = "(SELECT COUNT(*) FROM dbo.Lotes lx WHERE lx.IdManzana = m.IdManzana)",
                ["Superficie"] = Superficie("m.Geom"),
            },
            [], ["m.UV_MZA", "m.MZA", "m.UV"], true),

        [CatalogoCapas.Lotes] = new(
            "dbo.Lotes l LEFT JOIN dbo.Manzanas m ON m.IdManzana = l.IdManzana", "l.IdLote", "l.Geom",
            "CONCAT(N'Lote ', COALESCE(NULLIF(LTRIM(RTRIM(l.NroLote)), N''), N's/n'), " +
            "CASE WHEN NULLIF(LTRIM(RTRIM(m.MZA)), N'') IS NOT NULL THEN CONCAT(N' · ', LTRIM(RTRIM(m.MZA))) ELSE N'' END)",
            new Dictionary<string, string>
            {
                ["IdLote"] = "l.IdLote",
                ["NroLote"] = "l.NroLote",
                ["UV"] = "m.UV",
                ["MZA"] = "m.MZA",
                ["IdManzana"] = "l.IdManzana",
                ["CantidadCodigos"] = "(SELECT COUNT(*) FROM dbo.CodigosFijos cx WHERE cx.IdLote = l.IdLote)",
                ["Superficie"] = Superficie("l.Geom"),
            },
            [], ["l.NroLote"], true),

        [CatalogoCapas.CodigosFijos] = new(
            "dbo.CodigosFijos c LEFT JOIN dbo.Lotes l ON l.IdLote = c.IdLote LEFT JOIN dbo.Manzanas m ON m.IdManzana = l.IdManzana",
            "c.IdCodigo", "c.Geom",
            "CONCAT(N'Código ', c.CodFijo, N' · ', COALESCE(c.Nombre, N'(sin nombre)'))",
            new Dictionary<string, string>
            {
                ["IdCodigo"] = "c.IdCodigo",
                ["CodFijo"] = "c.CodFijo",
                ["CodF_SIG"] = "c.CodF_SIG",
                ["CodF_SQL"] = "c.CodF_SQL",
                ["Nombre"] = "c.Nombre",
                ["Estado"] = "c.Estado",
                ["FechaCambioEstado"] = "c.FechaCambioEstado",
                ["NroLote"] = "l.NroLote",
                ["UV"] = "m.UV",
                ["MZA"] = "m.MZA",
                ["Longitud"] = "c.Longitud",
                ["Latitud"] = "c.Latitud",
            },
            ["Estado"], ["c.Nombre", "c.CodF_SIG", "CONVERT(NVARCHAR(30), c.CodFijo)"], false),

        [CatalogoCapas.Vias] = new(
            "dbo.Vias v", "v.IdVia", "v.Geom",
            "COALESCE(NULLIF(LTRIM(RTRIM(v.Nombre)), N''), CONCAT(N'Vía sin nombre (', COALESCE(v.TipoVia, N'?'), N')'))",
            new Dictionary<string, string>
            {
                ["IdVia"] = "v.IdVia",
                ["Nombre"] = "v.Nombre",
                ["TipoVia"] = "v.TipoVia",
                ["OSMID"] = "v.OSMID",
                ["OBJECTID"] = "v.OBJECTID",
                ["LongitudMetros"] = "ROUND(geography::STGeomFromWKB(v.Geom.STAsBinary(), 4326).MakeValid().STLength(), 1)",
            },
            [], ["v.Nombre"], false),
    };

    public static SqlCapa Obtener(CapaDefinicion capa) => Capas[capa.Id];

    /// <summary>Lista de columnas ", expr AS [Clave]" para los campos indicados (vacía si no hay campos).</summary>
    public static string Columnas(SqlCapa sc, IEnumerable<string> claves) =>
        string.Concat(claves.Select(k => $", {sc.Campos[k]} AS [{k}]"));

    /// <summary>Verificación de arranque/pruebas: todo campo del catálogo tiene su expresión SQL.</summary>
    public static IEnumerable<string> CamposSinExpresion() =>
        CatalogoCapas.Todas.SelectMany(c => c.Campos.Where(f => !Capas[c.Id].Campos.ContainsKey(f.Clave))
            .Select(f => $"{c.Id}.{f.Clave}"));
}
