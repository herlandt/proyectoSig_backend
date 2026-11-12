using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Migracion;
using VisorDatosSIG.Infrastructure.Geo;

namespace VisorDatosSIG.Tests;

/// <summary>Validación y migración con los datos oficiales (carpeta DatosSIG_Reproj) y un repositorio simulado.</summary>
public class PruebasMigracion
{
    private static readonly LectorShapefileNts Lector = new();

    /// <summary>Busca la carpeta de datos oficiales subiendo desde el directorio de la prueba.</summary>
    private static string CarpetaDatos()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidata = Path.Combine(d.FullName, "DatosSIG_Reproj");
            if (Directory.Exists(candidata)) return candidata;
        }
        throw new DirectoryNotFoundException("No se encontró la carpeta DatosSIG_Reproj con los SHP oficiales.");
    }

    private static (DefinicionCapaMigracion Def, ArchivosShapefile Archivos) Capa(string id) =>
        DefinicionesMigracion.DetectarEnCarpeta(CarpetaDatos())
            .Where(t => t.Definicion?.Id == id).Select(t => (t.Definicion!, t.Archivos)).Single();

    [Fact]
    public void Se_reconocen_las_cuatro_capas_por_nombre()
    {
        var ids = DefinicionesMigracion.DetectarEnCarpeta(CarpetaDatos()).Select(t => t.Definicion?.Id).ToList();
        Assert.Equal(["manzanas", "lotes", "codigosfijos", "vias"], ids);
    }

    [Theory]
    [InlineData("manzanas", 863, 863, 0, 0)]
    [InlineData("lotes", 15281, 15281, 0, 1)]          // 1 geometría inválida reparada (FID 4915)
    [InlineData("codigosfijos", 6271, 6261, 10, 0)]    // 10 duplicados exactos (CodF_SQL + posición)
    [InlineData("vias", 578, 578, 0, 0)]
    public void Validacion_de_los_datos_oficiales(string capa, int leidos, int validos, int duplicados, int reparados)
    {
        var (def, archivos) = Capa(capa);
        var r = new ValidadorShapefile(Lector).Validar(def, archivos, confirmarSinPrj: false);
        Assert.False(r.Bloqueada);
        Assert.Equal(leidos, r.Leidos);
        Assert.Equal(validos, r.Validos);
        Assert.Equal(duplicados, r.Duplicados);
        Assert.Equal(reparados, r.Reparados);
        Assert.Equal(0, r.Fallidos);
    }

    [Fact]
    public void Latitud_se_toma_de_la_geometria_y_no_del_campo_Latid()
    {
        var (def, archivos) = Capa("codigosfijos");
        var fila = new ValidadorShapefile(Lector).VistaPrevia(def, archivos, 1)[0];
        Assert.Equal(fila.Origen["Longi"], fila.Origen["Latid"]);           // el DBF repite la longitud
        var latitud = (double)fila.Destino!["Latitud"]!;
        Assert.InRange(latitud, -16.45, -16.30);                          // San Ignacio de Velasco
        Assert.InRange((double)fila.Destino["Longitud"]!, -61.01, -60.91);
    }

    [Fact]
    public void Prj_ausente_bloquea_salvo_confirmacion_explicita()
    {
        using var tmp = CopiaTemporal("vias", sin: ".prj");
        var validador = new ValidadorShapefile(Lector);
        var (_, bloqueado) = validador.ValidarEstructura(DefinicionesMigracion.Vias, tmp.Archivos, confirmarSinPrj: false);
        Assert.True(bloqueado.HayBloqueantes);
        Assert.Contains(bloqueado.Detalle, i => i.Codigo == "BLOQ_PRJ_AUSENTE");

        var (_, confirmado) = validador.ValidarEstructura(DefinicionesMigracion.Vias, tmp.Archivos, confirmarSinPrj: true);
        Assert.False(confirmado.HayBloqueantes);
        Assert.Contains(confirmado.Detalle, i => i.Codigo == "PRJ_AUSENTE_CONFIRMADO");
    }

    [Fact]
    public void Prj_proyectado_se_bloquea()
    {
        using var tmp = CopiaTemporal("vias");
        File.WriteAllText(tmp.Archivos.Prj!, "PROJCS[\"WGS_1984_UTM_Zone_20S\",GEOGCS[\"GCS_WGS_1984\"]]");
        var (_, inc) = new ValidadorShapefile(Lector).ValidarEstructura(DefinicionesMigracion.Vias, tmp.Archivos, false);
        Assert.Contains(inc.Detalle, i => i.Codigo == "BLOQ_PRJ_NO_WGS84");
    }

    [Fact]
    public async Task CA02_archivo_incompleto_no_modifica_la_base()
    {
        using var tmp = CopiaTemporal("vias", sin: ".dbf");
        var repo = new RepositorioSimulado();
        var servicio = new MigracionServicio(Lector, repo);
        var r = await servicio.EjecutarAsync(Solicitud(new CapaAMigrar(DefinicionesMigracion.Vias, tmp.Archivos)), null, CancellationToken.None);

        Assert.Equal(EstadosMigracion.Fallida, r.Estado);
        Assert.False(repo.TransaccionIniciada);                           // nunca se abrió la transacción
        Assert.Contains(repo.Incidencias, i => i.Codigo == "BLOQ_ARCHIVO_FALTANTE");
        Assert.Equal(EstadosMigracion.Fallida, repo.EstadosFinales.Single());
    }

    [Fact]
    public async Task CA03_error_durante_la_carga_revierte_la_transaccion()
    {
        var repo = new RepositorioSimulado { FallarAlInsertarCapa = "lotes" };
        var (dm, am) = Capa("manzanas");
        var (dl, al) = Capa("lotes");
        var r = await new MigracionServicio(Lector, repo).EjecutarAsync(
            Solicitud(new CapaAMigrar(dm, am), new CapaAMigrar(dl, al)), null, CancellationToken.None);

        Assert.Equal(EstadosMigracion.Revertida, r.Estado);
        Assert.True(repo.Transaccion!.Revertida);
        Assert.False(repo.Transaccion.Confirmada);
        Assert.All(r.Capas, c => Assert.Equal(0, c.Conteos.Insertados));  // nada queda como insertado
        Assert.All(repo.EstadosFinales, e => Assert.Equal(EstadosMigracion.Revertida, e));
    }

    [Fact]
    public async Task RF_MIG_12_cancelar_revierte_sin_carga_parcial()
    {
        using var cts = new CancellationTokenSource();
        var repo = new RepositorioSimulado { AlInsertar = () => cts.Cancel() };
        var (dl, al) = Capa("lotes");
        var r = await new MigracionServicio(Lector, repo).EjecutarAsync(Solicitud(new CapaAMigrar(dl, al)), null, cts.Token);

        Assert.Equal(EstadosMigracion.Cancelada, r.Estado);
        Assert.True(repo.Transaccion!.Revertida);
        Assert.False(repo.Transaccion.Confirmada);
    }

    [Fact]
    public async Task Migracion_exitosa_inserta_por_lotes_y_confirma()
    {
        var repo = new RepositorioSimulado();
        var (dv, av) = Capa("vias");
        var r = await new MigracionServicio(Lector, repo).EjecutarAsync(Solicitud(new CapaAMigrar(dv, av)), null, CancellationToken.None);

        Assert.Equal(EstadosMigracion.Completada, r.Estado);
        Assert.True(repo.Transaccion!.Confirmada);
        Assert.Equal(578, repo.Transaccion.FilasInsertadas);
        Assert.Equal(1, repo.Transaccion.Lotes);                          // 578 filas < lote de 1000
        Assert.All(repo.Transaccion.Geometrias, g => Assert.Equal(4326, g.SRID));
    }

    [Fact]
    public async Task Modo_anexar_no_duplica_lo_existente()
    {
        var (dv, av) = Capa("vias");
        var repo = new RepositorioSimulado { ClavesExistentes = ["1", "2", "3"] };   // OBJECTID ya cargados
        var r = await new MigracionServicio(Lector, repo).EjecutarAsync(
            Solicitud(new CapaAMigrar(dv, av)) with { Modo = ModoCarga.Anexar }, null, CancellationToken.None);
        Assert.Equal(575, repo.Transaccion!.FilasInsertadas);
        Assert.Equal(3, r.Capas.Single().Conteos.Omitidos);
        Assert.False(repo.Transaccion.ReemplazoPreparado);
    }

    [Fact]
    public async Task Error_al_iniciar_transaccion_cierra_todas_las_bitacoras_como_fallidas()
    {
        var repo = new RepositorioSimulado { FallarAlIniciarTransaccion = true };
        var (dm, am) = Capa("manzanas");
        var (dv, av) = Capa("vias");
        var r = await new MigracionServicio(Lector, repo).EjecutarAsync(
            Solicitud(new CapaAMigrar(dm, am), new CapaAMigrar(dv, av)), null, CancellationToken.None);

        Assert.Equal(EstadosMigracion.Fallida, r.Estado);
        Assert.Contains("No se pudo abrir la transacción simulada", r.Mensaje);
        Assert.Null(repo.Transaccion);
        Assert.Equal(2, repo.EstadosFinales.Count);
        Assert.All(repo.EstadosFinales, e => Assert.Equal(EstadosMigracion.Fallida, e));
        Assert.All(repo.MensajesFinales, m => Assert.Contains("No se pudo abrir la transacción simulada", m));
        Assert.All(r.Capas, c => Assert.Equal(0, c.Conteos.Insertados));
    }

    [Fact]
    public async Task Error_al_iniciar_y_al_cerrar_auditoria_devuelve_fallida_sin_lanzar()
    {
        var repo = new RepositorioSimulado { FallarAlIniciarTransaccion = true, FallarAlFinalizar = true };
        var (dv, av) = Capa("vias");
        var r = await new MigracionServicio(Lector, repo).EjecutarAsync(
            Solicitud(new CapaAMigrar(dv, av)), null, CancellationToken.None);

        Assert.Equal(EstadosMigracion.Fallida, r.Estado);
        Assert.Contains("No se pudo abrir la transacción simulada", r.Mensaje);
        Assert.Contains("No se pudo cerrar el registro de auditoría", r.Mensaje);
        Assert.Null(repo.Transaccion);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Error_de_auditoria_despues_del_commit_conserva_la_carga_y_avisa(bool fallaCierre, bool fallanIncidencias)
    {
        var repo = new RepositorioSimulado { FallarAlFinalizar = fallaCierre, FallarAlRegistrarIncidencias = fallanIncidencias };
        var (dv, av) = Capa("vias");
        var r = await new MigracionServicio(Lector, repo).EjecutarAsync(
            Solicitud(new CapaAMigrar(dv, av)), null, CancellationToken.None);

        Assert.Equal(EstadosMigracion.Completada, r.Estado);
        Assert.True(r.Exitosa);
        Assert.Equal("Datos cargados; no se pudo cerrar el registro de auditoría", r.Mensaje);
        Assert.True(repo.Transaccion!.Confirmada);
        Assert.False(repo.Transaccion.Revertida);
        Assert.Equal(578, r.Capas.Single().Conteos.Insertados);
    }

    [Fact]
    public async Task Nueva_migracion_marca_interrumpidas_solo_las_bitacoras_antiguas_en_curso()
    {
        var ahora = DateTime.Now;
        var repo = new RepositorioSimulado
        {
            BitacorasPrevias =
            [
                (ahora.AddHours(-3), EstadosMigracion.EnCurso),
                (ahora.AddHours(-1), EstadosMigracion.EnCurso),
                (ahora.AddHours(-3), EstadosMigracion.Completada),
            ],
        };
        var (dv, av) = Capa("vias");
        var r = await new MigracionServicio(Lector, repo).EjecutarAsync(
            Solicitud(new CapaAMigrar(dv, av)), null, CancellationToken.None);

        Assert.Equal(EstadosMigracion.Completada, r.Estado);
        Assert.Equal("interrumpidas", repo.Operaciones[0]);
        Assert.Equal("iniciar", repo.Operaciones[1]);
        Assert.Equal(EstadosMigracion.Interrumpida, repo.BitacorasPrevias[0].Estado);
        Assert.Equal(EstadosMigracion.EnCurso, repo.BitacorasPrevias[1].Estado);
        Assert.Equal(EstadosMigracion.Completada, repo.BitacorasPrevias[2].Estado);
    }

    // ------------------------------------------------------------------ utilidades

    private static SolicitudMigracion Solicitud(params CapaAMigrar[] capas) => new("pruebas", "equipo", ModoCarga.Reemplazar, capas);

    private static Temporal CopiaTemporal(string capa, string? sin = null)
    {
        var (_, archivos) = Capa(capa);
        var dir = Directory.CreateTempSubdirectory("visor_prueba_");
        foreach (var f in Directory.EnumerateFiles(archivos.Carpeta, archivos.Nombre + ".*"))
            if (sin is null || !string.Equals(Path.GetExtension(f), sin, StringComparison.OrdinalIgnoreCase))
                File.Copy(f, Path.Combine(dir.FullName, Path.GetFileName(f)));
        return new Temporal(dir, ArchivosShapefile.Desde(Path.Combine(dir.FullName, archivos.Nombre + ".shp")));
    }

    private sealed record Temporal(DirectoryInfo Directorio, ArchivosShapefile Archivos) : IDisposable
    {
        public void Dispose() { try { Directorio.Delete(true); } catch (IOException) { } }
    }

    private sealed class RepositorioSimulado : IMigracionRepositorio
    {
        public bool TransaccionIniciada;
        public bool FallarAlIniciarTransaccion, FallarAlFinalizar, FallarAlRegistrarIncidencias;
        public TransaccionSimulada? Transaccion;
        public string? FallarAlInsertarCapa;
        public Action? AlInsertar;
        public HashSet<string> ClavesExistentes = [];
        public List<Incidencia> Incidencias = [];
        public List<string> EstadosFinales = [];
        public List<string> MensajesFinales = [];
        public List<string> Operaciones = [];
        public List<(DateTime FechaInicio, string Estado)> BitacorasPrevias = [];
        private int _id;

        public Task<string?> ProbarConexionAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<int> MarcarInterrumpidasAsync(CancellationToken ct)
        {
            Operaciones.Add("interrumpidas");
            int total = 0;
            var limite = DateTime.Now.AddHours(-2);
            for (int i = 0; i < BitacorasPrevias.Count; i++)
            {
                var b = BitacorasPrevias[i];
                if (b.Estado != EstadosMigracion.EnCurso || b.FechaInicio >= limite) continue;
                BitacorasPrevias[i] = (b.FechaInicio, EstadosMigracion.Interrumpida);
                total++;
            }
            return Task.FromResult(total);
        }
        public Task<int> IniciarBitacoraAsync(EntradaBitacoraMigracion e, CancellationToken ct)
        {
            Operaciones.Add("iniciar");
            return Task.FromResult(++_id);
        }
        public Task FinalizarBitacoraAsync(int id, string estado, ConteosCarga c, int ms, string? m, CancellationToken ct)
        {
            if (FallarAlFinalizar) throw new InvalidOperationException("Fallo de cierre simulado");
            EstadosFinales.Add(estado);
            MensajesFinales.Add(m ?? "");
            return Task.CompletedTask;
        }
        public Task RegistrarIncidenciasAsync(int id, IEnumerable<Incidencia> i, CancellationToken ct)
        {
            if (FallarAlRegistrarIncidencias) throw new InvalidOperationException("Fallo de incidencias simulado");
            Incidencias.AddRange(i);
            return Task.CompletedTask;
        }
        public Task<IMigracionTransaccion> IniciarTransaccionAsync(CancellationToken ct)
        {
            TransaccionIniciada = true;
            if (FallarAlIniciarTransaccion) throw new InvalidOperationException("No se pudo abrir la transacción simulada");
            Transaccion = new TransaccionSimulada(this);
            return Task.FromResult<IMigracionTransaccion>(Transaccion);
        }
        public Task<string> ReconstruirIndicesEspacialesAsync(CancellationToken ct) => Task.FromResult("");
        public Task<Pagina<MigracionHistorial>> ListarHistorialAsync(int p, int t, CancellationToken ct) => throw new NotSupportedException();
        public Task<(MigracionHistorial? Cabecera, IReadOnlyList<DetalleMigracion> Detalle)> ObtenerAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<DateTime?> UltimaActualizacionAsync(CancellationToken ct) => Task.FromResult<DateTime?>(null);
    }

    private sealed class TransaccionSimulada(RepositorioSimulado repo) : IMigracionTransaccion
    {
        public bool Confirmada, Revertida, ReemplazoPreparado;
        public int FilasInsertadas, Lotes;
        public List<NetTopologySuite.Geometries.Geometry> Geometrias = [];

        public Task PrepararReemplazoAsync(IReadOnlyCollection<string> capas, CancellationToken ct) { ReemplazoPreparado = true; return Task.CompletedTask; }
        public Task<HashSet<string>> ObtenerClavesExistentesAsync(string capa, CancellationToken ct) => Task.FromResult(repo.ClavesExistentes);
        public Task InsertarAsync(DefinicionCapaMigracion def, IReadOnlyList<FilaDestino> filas, CancellationToken ct)
        {
            if (def.Id == repo.FallarAlInsertarCapa) throw new InvalidOperationException("Pérdida de conexión simulada");
            repo.AlInsertar?.Invoke();
            FilasInsertadas += filas.Count;
            Lotes++;
            Geometrias.AddRange(filas.Select(f => f.Geometria));
            return Task.CompletedTask;
        }
        public Task<int> RepararGeometriasInvalidasAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<ResultadoVinculos> ActualizarVinculosAsync(CancellationToken ct) => Task.FromResult(new ResultadoVinculos(0, 0, 0, 0));
        public Task<IReadOnlyDictionary<string, int>> ContarAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        public Task ConfirmarAsync(CancellationToken ct) { Confirmada = true; return Task.CompletedTask; }
        public Task RevertirAsync() { Revertida = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { if (!Confirmada) Revertida = true; return ValueTask.CompletedTask; }
    }
}
