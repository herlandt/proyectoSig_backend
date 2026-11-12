using System.Globalization;
using VisorDatosSIG.Application.Capas;

namespace VisorDatosSIG.Application.Migracion;

public enum OrigenValor { Campo, Fid, CoordenadaX, CoordenadaY, Constante }

public enum TipoDestino { Entero, TinyInt, Texto, Flotante }

/// <summary>Mapeo EXPLÍCITO de un campo origen a una columna destino (nunca por orden de columnas).</summary>
public sealed record MapeoCampo(
    string ColumnaDestino,
    TipoDestino Tipo,
    OrigenValor Origen,
    string? CampoOrigen = null,
    int Longitud = 0,
    object? Constante = null,
    string Regla = "")
{
    public string DescripcionOrigen => Origen switch
    {
        OrigenValor.Campo => CampoOrigen!,
        OrigenValor.Fid => "FID (n.º de registro SHP)",
        OrigenValor.CoordenadaX => "Geometría X (longitud)",
        OrigenValor.CoordenadaY => "Geometría Y (latitud)",
        OrigenValor.Constante => $"Constante {Constante}",
        _ => ""
    };

    public string DescripcionTipo => Tipo switch
    {
        TipoDestino.Texto => $"NVARCHAR({Longitud})",
        TipoDestino.Entero => "INT",
        TipoDestino.TinyInt => "TINYINT",
        TipoDestino.Flotante => "FLOAT",
        _ => ""
    };
}

public sealed record DefinicionCapaMigracion(
    string Id,
    string Nombre,
    string Tabla,
    int Orden,
    string[] PatronesArchivo,
    string[] TiposGeometria,
    IReadOnlyList<MapeoCampo> Mapeo,
    string DescripcionClave,
    Func<IReadOnlyDictionary<string, object?>, string>? ClavePersonalizada = null)
{
    public IEnumerable<string> CamposOrigenRequeridos =>
        Mapeo.Where(m => m.Origen == OrigenValor.Campo).Select(m => m.CampoOrigen!);
}

/// <summary>
/// Matriz de mapeo SHP → SQL de las cuatro capas, según el diseño físico oficial
/// (ScriptDatabaseV13/01_CrearBD.sql). Ver docs/MapeoSHP-SQL.md.
/// </summary>
public static class DefinicionesMigracion
{
    public static readonly DefinicionCapaMigracion Manzanas = new(
        CatalogoCapas.Manzanas, "Manzanas", "dbo.Manzanas", 1,
        ["MZA", "MANZANA"], ["Polygon", "MultiPolygon"],
        [
            new("IdOrigen", TipoDestino.Entero, OrigenValor.Fid, Regla: "El campo Id del SHP vale 0 en todas las filas; se conserva el FID."),
            new("UV_MZA", TipoDestino.Texto, OrigenValor.Campo, "UV_MZA", 20, Regla: "Recortar espacios; vacío → NULL"),
            new("UV", TipoDestino.Texto, OrigenValor.Campo, "UV", 15, Regla: "Recortar espacios; vacío → NULL"),
            new("MZA", TipoDestino.Texto, OrigenValor.Campo, "MZA", 10, Regla: "Recortar espacios; vacío → NULL"),
        ],
        "IdOrigen (FID del SHP)");

    public static readonly DefinicionCapaMigracion Lotes = new(
        CatalogoCapas.Lotes, "Lotes", "dbo.Lotes", 2,
        ["LOTE"], ["Polygon", "MultiPolygon"],
        [
            new("IdOrigen", TipoDestino.Entero, OrigenValor.Fid, Regla: "El campo Id del SHP vale 0 en todas las filas; se conserva el FID."),
            new("NroLote", TipoDestino.Texto, OrigenValor.Campo, "NroLote", 15, Regla: "Recortar espacios; vacío → NULL"),
        ],
        "IdOrigen (FID del SHP)");

    public static readonly DefinicionCapaMigracion CodigosFijos = new(
        CatalogoCapas.CodigosFijos, "Códigos fijos", "dbo.CodigosFijos", 3,
        ["CODIGOFIJO", "CODIGO_FIJO", "CODIGOSFIJOS", "CODFIJO", "CODF"], ["Point"],
        [
            new("CodF_SQL", TipoDestino.Entero, OrigenValor.Campo, "CodF_SQL"),
            new("CodF_SIG", TipoDestino.Texto, OrigenValor.Campo, "CodF_SIG", 25, Regla: "Recortar espacios; vacío → NULL"),
            new("CodFijo", TipoDestino.Entero, OrigenValor.Campo, "CodFijo"),
            new("Nombre", TipoDestino.Texto, OrigenValor.Campo, "Nombre", 120, Regla: "Recortar espacios; vacío → NULL"),
            new("Estado", TipoDestino.TinyInt, OrigenValor.Constante, Constante: (byte)1, Regla: "Estado inicial 1 = Normal"),
            new("Longitud", TipoDestino.Flotante, OrigenValor.CoordenadaX, Regla: "Se toma de la geometría (el DBF trae Longi)"),
            new("Latitud", TipoDestino.Flotante, OrigenValor.CoordenadaY, Regla: "Se toma de la geometría: el campo Latid del DBF repite la longitud"),
        ],
        "CodF_SQL + posición (6 decimales)",
        v => string.Create(CultureInfo.InvariantCulture,
            $"{v["CodF_SQL"]}|{Math.Round(Convert.ToDouble(v["Longitud"], CultureInfo.InvariantCulture), 6)}|{Math.Round(Convert.ToDouble(v["Latitud"], CultureInfo.InvariantCulture), 6)}"));

    public static readonly DefinicionCapaMigracion Vias = new(
        CatalogoCapas.Vias, "Vías", "dbo.Vias", 4,
        ["VIAS", "VIA", "CALLE"], ["LineString", "MultiLineString"],
        [
            new("OBJECTID", TipoDestino.Entero, OrigenValor.Campo, "OBJECTID"),
            new("Nombre", TipoDestino.Texto, OrigenValor.Campo, "Nombre", 40, Regla: "Recortar espacios; vacío → NULL"),
            new("TipoVia", TipoDestino.Texto, OrigenValor.Campo, "type", 30, Regla: "Clasificación OSM (residential, track...)"),
            new("OSMID", TipoDestino.Texto, OrigenValor.Campo, "OSMID", 20, Regla: "Número → texto sin decimales"),
        ],
        "OBJECTID");

    public static readonly IReadOnlyList<DefinicionCapaMigracion> Todas = [Manzanas, Lotes, CodigosFijos, Vias];

    public static DefinicionCapaMigracion? Buscar(string? id) =>
        Todas.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reconoce la capa por el nombre del archivo (RF-MIG-03). Se evalúan primero los patrones más específicos.</summary>
    public static DefinicionCapaMigracion? ReconocerPorNombre(string nombreArchivo)
    {
        var nombre = Path.GetFileNameWithoutExtension(nombreArchivo).ToUpperInvariant().Replace(" ", "");
        foreach (var def in new[] { CodigosFijos, Manzanas, Lotes, Vias })
            if (def.PatronesArchivo.Any(p => nombre.Contains(p, StringComparison.Ordinal)))
                return def;
        return null;
    }

    public static string ClaveNatural(DefinicionCapaMigracion def, IReadOnlyDictionary<string, object?> valores) =>
        def.ClavePersonalizada?.Invoke(valores)
        ?? Convert.ToString(valores[def.Mapeo[0].ColumnaDestino], CultureInfo.InvariantCulture) ?? "";

    /// <summary>Columnas que forman la clave natural (para leerlas desde la base en modo Anexar).</summary>
    public static string[] ColumnasClave(DefinicionCapaMigracion def) =>
        def.ClavePersonalizada is null ? [def.Mapeo[0].ColumnaDestino] : ["CodF_SQL", "Longitud", "Latitud"];

    /// <summary>Busca en una carpeta los conjuntos SHP y los asocia a las capas conocidas (RF-MIG-02/03).</summary>
    public static IReadOnlyList<(DefinicionCapaMigracion? Definicion, ArchivosShapefile Archivos)> DetectarEnCarpeta(string carpeta)
    {
        if (!Directory.Exists(carpeta)) throw new Comun.ErrorValidacion($"La carpeta '{carpeta}' no existe.");
        return Directory.EnumerateFiles(carpeta, "*.*")
            .Where(f => string.Equals(Path.GetExtension(f), ".shp", StringComparison.OrdinalIgnoreCase))
            .Select(f => (ReconocerPorNombre(f), ArchivosShapefile.Desde(f)))
            .OrderBy(t => t.Item1?.Orden ?? 99)
            .ToList();
    }
}
