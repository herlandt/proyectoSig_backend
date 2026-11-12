using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Operaciones;
using VisorDatosSIG.Application.Rutas;
using VisorDatosSIG.Domain;
using VisorDatosSIG.Infrastructure.Geo;

namespace VisorDatosSIG.Tests;

/// <summary>Reglas de la parte 2: asignación con ruta acumulada, registro en campo con GPS obligatorio, posiciones y evidencias.</summary>
public class PruebasOperaciones
{
    private const double X0 = -61.0, Y0 = -16.38, Paso = 0.001;
    private static Coordenada C(double i, double j) => new(X0 + i * Paso, Y0 + j * Paso);
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1];

    // ------------------------------------------------------------------ asignaciones

    [Fact]
    public async Task Sin_punto_de_partida_no_se_calcula()
    {
        var (servicio, _) = Asignaciones();
        var e = await Assert.ThrowsAsync<ErrorValidacion>(() => servicio.CalcularAsync(new("Corte", 5, 3, null, null), default));
        Assert.Equal("partida", e.Campo);
    }

    [Fact]
    public async Task Solo_se_asigna_a_un_trabajador_con_el_rol_del_tipo()
    {
        var (servicio, repo) = Asignaciones();
        repo.Trabajadores.Clear();
        var e = await Assert.ThrowsAsync<ErrorValidacion>(() => servicio.CalcularAsync(Solicitud(3), default));
        Assert.Equal("trabajador", e.Campo);
    }

    [Fact]
    public async Task La_cantidad_respeta_el_maximo_de_paradas_contando_los_pendientes()
    {
        var (servicio, repo) = Asignaciones();
        repo.Configuracion = repo.Configuracion with { MaximoParadas = 3 };
        repo.Activa = Activa(Pendiente(1, 100, C(2, 2)));
        var e = await Assert.ThrowsAsync<ErrorValidacion>(() => servicio.CalcularAsync(Solicitud(3), default));
        Assert.Equal("cantidad", e.Campo);
        Assert.Contains("entre 0 y 2", e.Message);
    }

    [Fact]
    public async Task La_ruta_se_acumula_con_los_pendientes_y_sigue_la_numeracion_de_lo_realizado()
    {
        var (servicio, repo) = Asignaciones();
        repo.Activa = Activa(Realizada(1, 90, C(0.1, 0)), Pendiente(2, 100, C(2, 2)));

        var p = await servicio.CalcularAsync(Solicitud(2), default);

        Assert.Equal(1, p.PendientesPrevios);
        Assert.Equal(2, p.Nuevas);
        Assert.Equal([2, 3, 4], p.Paradas.Select(x => x.Orden));          // la parada realizada conserva el 1
        Assert.Contains(p.Paradas, x => x.Codigo.IdCodigo == 100 && x.EraPendiente);
        Assert.Equal(100, p.Paradas[^1].Codigo.IdCodigo);                 // el pendiente lejano queda al final
        Assert.True(p.DuracionMin > 3 * 5);                               // 3 paradas × 5 min + el viaje
    }

    [Fact]
    public async Task Guardar_persiste_el_plan_recalculado_en_el_servidor()
    {
        var (servicio, repo) = Asignaciones();
        var (id, propuesta) = await servicio.GuardarAsync(Solicitud(2), idAsignadoPor: 1, default);

        Assert.Equal(77, id);
        var plan = Assert.Single(repo.Guardados);
        Assert.Equal(TipoAsignacion.Corte, plan.Tipo);
        Assert.Equal(1, plan.IdAsignadoPor);
        Assert.Equal(propuesta.Paradas.Select(x => x.Codigo.IdCodigo), plan.Paradas.Select(x => x.IdCodigo));
        Assert.True(plan.Ruta.Count >= 2);
    }

    [Fact]
    public async Task Tipo_desconocido_es_error_de_validacion()
    {
        var (servicio, _) = Asignaciones();
        var e = await Assert.ThrowsAsync<ErrorValidacion>(() => servicio.CalcularAsync(new("Lectura", 5, 1, Y0, X0), default));
        Assert.Equal("tipo", e.Campo);
    }

    // ------------------------------------------------------------------ registro en campo

    [Fact]
    public async Task Sin_GPS_no_se_registra_nada()
    {
        var (campo, repo, _) = Campo();
        var e = await Assert.ThrowsAsync<ErrorValidacion>(() =>
            campo.RegistrarAsync(5, 10, Datos(lat: null, lon: null), [Foto()], default));
        Assert.Equal("gps", e.Campo);
        Assert.Empty(repo.Ejecuciones);
    }

    [Fact]
    public async Task Un_corte_realizado_exige_lectura_y_foto_y_deja_el_codigo_cortado()
    {
        var (campo, repo, _) = Campo();
        await Assert.ThrowsAsync<ErrorValidacion>(() => campo.RegistrarAsync(5, 10, Datos(lectura: null), [Foto()], default));
        await Assert.ThrowsAsync<ErrorValidacion>(() => campo.RegistrarAsync(5, 10, Datos(), [], default));

        var r = await campo.RegistrarAsync(5, 10, Datos(), [Foto()], default);

        var e = Assert.Single(repo.Ejecuciones);
        Assert.Equal(ResultadosEjecucion.Cortado, e.Resultado);
        Assert.Equal(EstadosParada.Ejecutada, e.EstadoParada);
        Assert.Equal(EstadoServicio.Cortado, e.NuevoEstadoCodigo);
        Assert.True(r.DentroDelRadio);
        Assert.Equal(1, r.Fotos);
    }

    [Fact]
    public async Task Observado_exige_observacion_y_no_cambia_el_estado_del_codigo()
    {
        var (campo, repo, _) = Campo();
        await Assert.ThrowsAsync<ErrorValidacion>(() =>
            campo.RegistrarAsync(5, 10, Datos(resultado: "Observado", lectura: null), [], default));

        await campo.RegistrarAsync(5, 10, Datos(resultado: "Observado", lectura: null, observacion: "Casa cerrada"), [], default);

        var e = Assert.Single(repo.Ejecuciones);
        Assert.Equal(EstadosParada.Observada, e.EstadoParada);
        Assert.Null(e.NuevoEstadoCodigo);
        Assert.Equal("Casa cerrada", e.Observacion);
    }

    [Fact]
    public async Task Reconexion_realizada_vuelve_el_codigo_a_normal_y_no_acepta_cortado()
    {
        var (campo, repo, _) = Campo(TipoAsignacion.Reconexion);
        await Assert.ThrowsAsync<ErrorValidacion>(() => campo.RegistrarAsync(5, 10, Datos(resultado: "Cortado"), [Foto()], default));

        await campo.RegistrarAsync(5, 10, Datos(resultado: "Reconectado"), [Foto()], default);

        Assert.Equal(EstadoServicio.Normal, Assert.Single(repo.Ejecuciones).NuevoEstadoCodigo);
    }

    [Fact]
    public async Task Guardar_lejos_del_codigo_queda_marcado_fuera_del_radio()
    {
        var (campo, repo, _) = Campo();
        // El código está en C(0.5, 0); el GPS marca ≈330 m al norte.
        var r = await campo.RegistrarAsync(5, 10, Datos(lat: Y0 + 0.003, lon: X0 + 0.0005), [Foto()], default);

        Assert.False(r.DentroDelRadio);
        Assert.InRange(r.DistanciaAlPuntoMetros, 300, 360);
        Assert.False(Assert.Single(repo.Ejecuciones).DentroDelRadio);
    }

    [Fact]
    public async Task Una_tarea_ajena_o_ya_registrada_no_se_puede_registrar()
    {
        var (campo, _, _) = Campo();
        var e = await Assert.ThrowsAsync<ErrorValidacion>(() => campo.RegistrarAsync(99, 10, Datos(), [Foto()], default));
        Assert.Equal("tarea", e.Campo);
    }

    [Fact]
    public async Task Si_falla_el_registro_se_borran_las_fotos_guardadas()
    {
        var (campo, repo, almacen) = Campo();
        repo.FallarAlRegistrar = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => campo.RegistrarAsync(5, 10, Datos(), [Foto(), Foto()], default));

        Assert.Equal(2, almacen.Guardadas);
        Assert.Equal(2, almacen.Eliminadas);
    }

    [Fact]
    public async Task Las_posiciones_imposibles_se_descartan()
    {
        var (campo, repo, _) = Campo();
        var ahora = DateTime.Now;
        int guardadas = await campo.RegistrarPosicionesAsync(5, null,
        [
            new(ahora.AddMinutes(-2), Y0, X0, 5, 1),
            new(ahora.AddMinutes(-1), 0, 0, 5, 1),            // 0,0: GPS sin señal
            new(ahora.AddHours(2), Y0, X0, 5, 1),             // en el futuro
            new(ahora, double.NaN, X0, 5, 1),
            new(ahora, Y0, X0, -3, 1),                        // precisión negativa
        ], default);

        Assert.Equal(1, guardadas);
        Assert.Single(repo.Posiciones);
    }

    // ------------------------------------------------------------------ monitoreo y evidencias

    [Fact]
    public void El_recorrido_GPS_se_ajusta_a_las_calles()
    {
        var red = Cuadricula();
        var t = DateTime.Today.AddHours(8);
        // Esquina (0,0) → mitad de la calle x=1 entre y=0 y y=1, con una posición repetida (detenido).
        PosicionGps[] posiciones = [new(t, Y0, X0, 5, 0), new(t.AddSeconds(30), Y0, X0, 5, 0), new(t.AddMinutes(1), Y0 + 0.5 * Paso, X0 + Paso, 5, 1)];

        var (trazado, distancia) = MonitoreoServicio.AjustarARed(red, posiciones);

        double esperado = RedVial.Distancia(C(0, 0), C(1, 0)) + RedVial.Distancia(C(1, 0), C(1, 0.5));
        Assert.Equal(esperado, distancia, 1);
        Assert.Contains(trazado, c => Math.Abs(c.Lon - (X0 + Paso)) < 1e-9 && Math.Abs(c.Lat - Y0) < 1e-9);   // pasa por la esquina (1,0)
    }

    [Fact]
    public async Task El_almacen_solo_acepta_JPEG_o_PNG_y_no_sale_de_su_carpeta()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "visor-evidencias-" + Guid.NewGuid().ToString("N"));
        try
        {
            var almacen = new AlmacenEvidencias(carpeta);
            await Assert.ThrowsAsync<ErrorValidacion>(() => almacen.GuardarAsync(new MemoryStream("no es imagen"u8.ToArray()), default));

            var foto = await almacen.GuardarAsync(new MemoryStream(Jpeg), default);

            Assert.Equal("image/jpeg", foto.TipoContenido);
            Assert.EndsWith(".jpg", foto.NombreArchivo);
            Assert.Equal(64, foto.HashSha256.Length);
            using (var leido = almacen.Abrir(foto.NombreArchivo)) Assert.NotNull(leido);
            Assert.Null(almacen.Abrir("../../Windows/win.ini"));
        }
        finally
        {
            if (Directory.Exists(carpeta)) Directory.Delete(carpeta, recursive: true);
        }
    }

    [Fact]
    public void Dimensiones_de_un_cuadrado_de_100_m()
    {
        // Cuadrado de ≈100 m de lado a la latitud de San Ignacio.
        double dLat = 100 / (Math.PI / 180 * 6371008.8), dLon = dLat / Math.Cos(Y0 * Math.PI / 180);
        var f = new NetTopologySuite.Geometries.GeometryFactory();
        var cuadrado = f.CreatePolygon([new(X0, Y0), new(X0 + dLon, Y0), new(X0 + dLon, Y0 + dLat), new(X0, Y0 + dLat), new(X0, Y0)]);

        var etiquetas = VisorDatosSIG.Application.Capas.Dimensiones.Calcular(cuadrado).ToList();

        var lados = etiquetas.Where(e => e.Tipo == "lado").ToList();
        Assert.Equal(4, lados.Count);
        Assert.All(lados, l => Assert.InRange(double.Parse(l.Texto.Split(' ')[0], System.Globalization.CultureInfo.GetCultureInfo("es-BO")), 99.5, 100.5));
        Assert.All(lados, l => Assert.InRange(l.Angulo, -90, 90));
        var superficie = Assert.Single(etiquetas, e => e.Tipo == "superficie");
        Assert.InRange(VisorDatosSIG.Application.Capas.Dimensiones.Area(cuadrado, Math.Cos(Y0 * Math.PI / 180)), 9950, 10050);
        Assert.EndsWith("m²", superficie.Texto);
    }

    // ------------------------------------------------------------------ simulador de recorrido

    [Fact]
    public void El_simulador_recorre_las_paradas_pendientes_en_orden_y_se_detiene_en_cada_una()
    {
        var configuracion = ConfiguracionOperativa.PorDefecto with { VelocidadKmH = 18, MinutosPorParada = 2, IntervaloGpsSegundos = 10 };
        // Partida en C(0,0) y dos paradas a una y dos cuadras al este (≈107 m cada tramo); la ya realizada no se visita.
        var asignacion = Activa(Pendiente(2, 2, C(2, 0)), Realizada(3, 9, C(0, 2)), Pendiente(1, 1, C(1, 0)));
        var inicio = new DateTime(2026, 10, 30, 8, 0, 0);
        var posiciones = SimuladorRecorrido.Generar(asignacion, configuracion, inicio);

        Assert.Equal(inicio, posiciones[0].FechaHora);
        Assert.All(posiciones.Zip(posiciones.Skip(1)), par => Assert.Equal(TimeSpan.FromSeconds(10), par.Second.FechaHora - par.First.FechaHora));
        // Dos tramos de ≈107 m a 5 m/s (≈43 s) más dos pausas de 120 s.
        Assert.InRange((posiciones[^1].FechaHora - inicio).TotalSeconds, 270, 290);
        // Termina detenido en la última parada, a pocos metros por el error simulado del GPS.
        Assert.InRange(RedVial.Distancia(posiciones[^1].Ubicacion, C(2, 0)), 0, 7);
        Assert.Equal(0, posiciones[^1].VelocidadMs);
        Assert.All(posiciones, p => Assert.True(RedVial.Distancia(p.Ubicacion, C(0, 2)) > 100));
        // Reproducible: la misma semilla da el mismo recorrido.
        Assert.Equal(posiciones, SimuladorRecorrido.Generar(asignacion, configuracion, inicio));
    }

    [Fact]
    public void El_simulador_puede_limitarse_a_las_primeras_paradas()
    {
        var asignacion = Activa(Pendiente(1, 1, C(1, 0)), Pendiente(2, 2, C(2, 0)));
        var posiciones = SimuladorRecorrido.Generar(asignacion, ConfiguracionOperativa.PorDefecto, DateTime.Today, maximoParadas: 1);
        Assert.InRange(RedVial.Distancia(posiciones[^1].Ubicacion, C(1, 0)), 0, 7);
        Assert.All(posiciones, p => Assert.True(RedVial.Distancia(p.Ubicacion, C(2, 0)) > 50));
    }

    // ------------------------------------------------------------------ preparación

    private static RedVial Cuadricula()
    {
        var lineas = new List<IReadOnlyList<Coordenada>>();
        for (int k = 0; k <= 2; k++)
        {
            lineas.Add([C(k, 0), C(k, 1), C(k, 2)]);
            lineas.Add([C(0, k), C(1, k), C(2, k)]);
        }
        return new RedVial(lineas);
    }

    private static SolicitudRuta Solicitud(int cantidad) => new("Corte", 5, cantidad, Y0, X0);

    private static (AsignacionServicio, RepositorioFalso) Asignaciones()
    {
        var repo = new RepositorioFalso();
        repo.Candidatos.AddRange([Codigo(1, C(0.5, 0)), Codigo(2, C(1, 0.5)), Codigo(3, C(1.5, 1)), Codigo(4, C(0, 1.5))]);
        return (new AsignacionServicio(repo, new RedFalsa(Cuadricula())), repo);
    }

    private static (CampoServicio, RepositorioFalso, AlmacenFalso) Campo(TipoAsignacion tipo = TipoAsignacion.Corte)
    {
        var repo = new RepositorioFalso();
        repo.Tareas.Add(new TareaCampo(10, 3, tipo, EstadosAsignacion.EnCurso, 1, Codigo(1, C(0.5, 0)), 50, 5));
        var almacen = new AlmacenFalso();
        return (new CampoServicio(repo, almacen), repo, almacen);
    }

    private static DatosEjecucion Datos(string resultado = "Cortado", decimal? lectura = 1229, string? observacion = null,
        double? lat = Y0, double? lon = X0 + 0.5 * Paso) =>
        new(resultado, "A24AA0069427", lectura, observacion, lat, lon, 4, DateTime.Now);

    private static MemoryStream Foto() => new(Jpeg);

    private static CodigoOperativo Codigo(int id, Coordenada c) => new(id, 1000 + id, $"Titular {id}", 2, c.Lon, c.Lat);

    private static ParadaAsignada Pendiente(int orden, int id, Coordenada c) =>
        new(orden * 10, orden, EstadosParada.Pendiente, Codigo(id, c), 0, 0, []);

    private static ParadaAsignada Realizada(int orden, int id, Coordenada c) =>
        new(orden * 10, orden, EstadosParada.Ejecutada, Codigo(id, c), 0, 0, []);

    private static AsignacionActiva Activa(params ParadaAsignada[] paradas) =>
        new(3, TipoAsignacion.Corte, 5, EstadosAsignacion.EnCurso, DateTime.Today, C(0, 0), 0, 0, paradas);

    private sealed class RedFalsa(RedVial red) : IRedVialProveedor
    {
        public Task<RedVial> ObtenerAsync(CancellationToken ct) => Task.FromResult(red);
    }

    private sealed class AlmacenFalso : IAlmacenEvidencias
    {
        public int Guardadas, Eliminadas;
        public Task<FotoGuardada> GuardarAsync(Stream contenido, CancellationToken ct)
        {
            Guardadas++;
            return Task.FromResult(new FotoGuardada($"f{Guardadas}.jpg", "image/jpeg", (int)contenido.Length, new string('0', 64)));
        }
        public Stream? Abrir(string nombreArchivo) => null;
        public void Eliminar(string nombreArchivo) => Eliminadas++;
    }

    private sealed class RepositorioFalso : IOperacionesRepositorio
    {
        public ConfiguracionOperativa Configuracion = ConfiguracionOperativa.PorDefecto;
        public readonly List<Trabajador> Trabajadores = [new(5, "cortador", "Cortador de prueba", 0)];
        public readonly List<CodigoOperativo> Candidatos = [];
        public readonly List<TareaCampo> Tareas = [];
        public readonly List<PlanAGuardar> Guardados = [];
        public readonly List<EjecucionARegistrar> Ejecuciones = [];
        public readonly List<PosicionGps> Posiciones = [];
        public AsignacionActiva? Activa;
        public bool FallarAlRegistrar;

        public Task<ConfiguracionOperativa> ObtenerConfiguracionAsync(CancellationToken ct) => Task.FromResult(Configuracion);
        public Task GuardarConfiguracionAsync(ConfiguracionOperativa c, CancellationToken ct) { Configuracion = c; return Task.CompletedTask; }
        public Task<IReadOnlyList<Trabajador>> ListarTrabajadoresAsync(TipoAsignacion tipo, CancellationToken ct) => Task.FromResult<IReadOnlyList<Trabajador>>(Trabajadores);
        public Task<bool> EsTrabajadorAsync(int idUsuario, TipoAsignacion tipo, CancellationToken ct) => Task.FromResult(Trabajadores.Any(t => t.IdUsuario == idUsuario));
        public Task<IReadOnlyList<CodigoOperativo>> ListarCandidatosAsync(TipoAsignacion tipo, CancellationToken ct) => Task.FromResult<IReadOnlyList<CodigoOperativo>>(Candidatos);
        public Task<AsignacionActiva?> ObtenerActivaAsync(int idTrabajador, TipoAsignacion tipo, CancellationToken ct) => Task.FromResult(Activa);
        public Task<AsignacionActiva?> ObtenerAsignacionAsync(int idAsignacion, CancellationToken ct) => Task.FromResult(Activa);
        public Task<int> GuardarPlanAsync(PlanAGuardar plan, CancellationToken ct) { Guardados.Add(plan); return Task.FromResult(77); }
        public Task<bool> AnularAsync(int idAsignacion, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<TareaCampo>> ListarTareasAsync(int idTrabajador, CancellationToken ct) => Task.FromResult<IReadOnlyList<TareaCampo>>(Tareas);
        public Task<TareaCampo?> ObtenerTareaAsync(int idDetalle, int idTrabajador, CancellationToken ct) =>
            Task.FromResult(idTrabajador == 5 ? Tareas.FirstOrDefault(t => t.IdDetalle == idDetalle && Ejecuciones.All(e => e.IdDetalle != idDetalle)) : null);
        public Task<bool> IniciarRecorridoAsync(int idAsignacion, int idTrabajador, CancellationToken ct) => Task.FromResult(true);
        public Task RegistrarEjecucionAsync(EjecucionARegistrar e, IReadOnlyList<FotoGuardada> fotos, CancellationToken ct)
        {
            if (FallarAlRegistrar) throw new InvalidOperationException("Falla simulada de la base de datos.");
            Ejecuciones.Add(e);
            return Task.CompletedTask;
        }
        public Task<bool> AsignacionPerteneceAsync(int idAsignacion, int idTrabajador, CancellationToken ct) => Task.FromResult(true);
        public Task<int> RegistrarPosicionesAsync(int idTrabajador, int? idAsignacion, IReadOnlyList<PosicionGps> posiciones, CancellationToken ct)
        {
            Posiciones.AddRange(posiciones);
            return Task.FromResult(posiciones.Count);
        }
        public Task<IReadOnlyList<UltimaPosicion>> ListarUltimasPosicionesAsync(DateTime desde, CancellationToken ct) => Task.FromResult<IReadOnlyList<UltimaPosicion>>([]);
        public Task<IReadOnlyList<(int IdTrabajador, string Nombre, PosicionGps Posicion)>> ListarPosicionesAsync(DateOnly fecha, int? idTrabajador, TipoAsignacion? tipo, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(int, string, PosicionGps)>>([]);
        public Task<IReadOnlyList<(int IdTrabajador, ParadaRealizada Parada)>> ListarRealizadasAsync(DateOnly fecha, int? idTrabajador, TipoAsignacion? tipo, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<(int, ParadaRealizada)>>([]);
        public Task<Pagina<EjecucionRegistrada>> ListarEjecucionesAsync(FiltroEjecuciones filtro, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FotoEvidencia>> ListarFotosAsync(int idDetalle, CancellationToken ct) => throw new NotSupportedException();
        public Task<FotoEvidencia?> ObtenerFotoAsync(int idFoto, CancellationToken ct) => throw new NotSupportedException();
        public Task<ResumenOperativo> ObtenerResumenAsync(DateOnly dia, CancellationToken ct) => throw new NotSupportedException();
    }
}
