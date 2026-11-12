namespace VisorDatosSIG.Application.Migracion;

public sealed record ResultadoValidacionCapa(
    DefinicionCapaMigracion Definicion,
    ArchivosShapefile Archivos,
    InfoShapefile? Info,
    RegistroIncidencias Incidencias,
    int Leidos,
    int Validos,
    int Fallidos,
    int Duplicados,
    int Reparados)
{
    /// <summary>Hay un problema que impide migrar (archivo faltante, PRJ inválido, esquema incompatible).</summary>
    public bool Bloqueada => Incidencias.HayBloqueantes;
}

public sealed record FilaVistaPrevia(int Fid, IReadOnlyDictionary<string, object?> Origen, IReadOnlyDictionary<string, object?>? Destino, string Geometria);

/// <summary>Valida integridad, referencia espacial, esquema y geometrías antes de migrar (RF-MIG-04).</summary>
public sealed class ValidadorShapefile(ILectorShapefile lector)
{
    /// <summary>Validación estructural (rápida): archivos, PRJ WGS 84, tipo geométrico y campos.</summary>
    public (InfoShapefile? Info, RegistroIncidencias Incidencias) ValidarEstructura(
        DefinicionCapaMigracion def, ArchivosShapefile archivos, bool confirmarSinPrj)
    {
        var inc = new RegistroIncidencias();
        foreach (var falta in archivos.Faltantes(exigirPrj: false))
            inc.Agregar(NivelIncidencia.Error, "BLOQ_ARCHIVO_FALTANTE", $"Falta el archivo {archivos.Nombre}{falta}; la capa no se puede migrar.");
        if (inc.HayBloqueantes) return (null, inc);

        if (archivos.Prj is null)
        {
            if (confirmarSinPrj)
                inc.Agregar(NivelIncidencia.Advertencia, "PRJ_AUSENTE_CONFIRMADO",
                    "No existe .prj; el operador confirmó explícitamente que la capa está en WGS 84 (SRID 4326).");
            else
                inc.Agregar(NivelIncidencia.Error, "BLOQ_PRJ_AUSENTE",
                    "No existe el archivo .prj: la carga queda bloqueada hasta que se confirme la referencia espacial.");
        }

        InfoShapefile info;
        try { info = lector.Inspeccionar(archivos); }
        catch (Exception ex)
        {
            inc.Agregar(NivelIncidencia.Error, "BLOQ_LECTURA", $"No se pudo leer el Shapefile: {ex.Message}");
            return (null, inc);
        }

        if (info.ContenidoPrj is { } prj && !EsWgs84(prj))
            inc.Agregar(NivelIncidencia.Error, "BLOQ_PRJ_NO_WGS84",
                "La referencia espacial del .prj no es WGS 84 geográfico (SRID 4326). La conversión de sistemas está fuera de alcance.",
                valor: prj.Length > 200 ? prj[..200] : prj);

        bool tipoCompatible = def.TiposGeometria.Any(t => info.TipoGeometria.StartsWith(t switch
        {
            "Polygon" or "MultiPolygon" => "Polygon",
            "LineString" or "MultiLineString" => "PolyLine",
            _ => t
        }, StringComparison.OrdinalIgnoreCase));
        if (!tipoCompatible)
            inc.Agregar(NivelIncidencia.Error, "BLOQ_TIPO_GEOMETRIA",
                $"El SHP es de tipo {info.TipoGeometria} y la tabla {def.Tabla} espera {string.Join("/", def.TiposGeometria)}.");

        var campos = info.Campos.Select(c => c.Nombre).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var requerido in def.CamposOrigenRequeridos.Where(c => !campos.Contains(c)))
            inc.Agregar(NivelIncidencia.Error, "BLOQ_CAMPO_FALTANTE",
                $"El DBF no tiene el campo '{requerido}' que exige el mapeo hacia {def.Tabla}.", campo: requerido);

        if (info.TieneZ)
            inc.Agregar(NivelIncidencia.Info, "Z_DESCARTADA", "La capa trae coordenada Z; se almacena en 2D (X = longitud, Y = latitud).");
        if (info.Registros == 0)
            inc.Agregar(NivelIncidencia.Advertencia, "CAPA_VACIA", "El Shapefile no tiene registros.");
        return (info, inc);
    }

    /// <summary>Validación completa: estructura + lectura y transformación de todos los registros (sin escribir en la base).</summary>
    public ResultadoValidacionCapa Validar(DefinicionCapaMigracion def, ArchivosShapefile archivos, bool confirmarSinPrj,
        IProgress<int>? progreso = null, CancellationToken ct = default)
    {
        var (info, inc) = ValidarEstructura(def, archivos, confirmarSinPrj);
        if (info is null || inc.HayBloqueantes)
            return new(def, archivos, info, inc, 0, 0, 0, 0, 0);

        var transformador = new TransformadorCapa(def, inc);
        int leidos = 0, validos = 0, fallidos = 0, duplicados = 0, reparados = 0;
        foreach (var registro in lector.Leer(archivos, ct))
        {
            leidos++;
            var r = transformador.Transformar(registro);
            if (r.Reparada) reparados++;
            switch (r.Estado)
            {
                case EstadoFila.Valida: validos++; break;
                case EstadoFila.Fallida: fallidos++; break;
                case EstadoFila.Duplicada: duplicados++; break;
            }
            if (leidos % 500 == 0) progreso?.Report(leidos);
        }
        progreso?.Report(leidos);
        return new(def, archivos, info, inc, leidos, validos, fallidos, duplicados, reparados);
    }

    /// <summary>Primeros registros con su valor origen y destino (RF-MIG-05).</summary>
    public IReadOnlyList<FilaVistaPrevia> VistaPrevia(DefinicionCapaMigracion def, ArchivosShapefile archivos, int cantidad = 20)
    {
        var transformador = new TransformadorCapa(def, new RegistroIncidencias());
        return lector.Leer(archivos).Take(cantidad).Select(r =>
        {
            var t = transformador.Transformar(r);
            var geometria = r.Geometria is null ? "(vacía)" : $"{r.Geometria.GeometryType} · {r.Geometria.NumPoints} vértices";
            return new FilaVistaPrevia(r.Fid, r.Atributos, t.Fila?.Valores, geometria);
        }).ToList();
    }

    public static bool EsWgs84(string prj)
    {
        var p = prj.ToUpperInvariant();
        if (p.TrimStart().StartsWith("PROJCS", StringComparison.Ordinal)) return false;
        return p.Contains("WGS_1984") || p.Contains("WGS 84") || p.Contains("WGS84");
    }
}
