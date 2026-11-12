using VisorDatosSIG.Application.Comun;
using VisorDatosSIG.Application.Rutas;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Application.Operaciones;

/// <summary>Parámetros de dbo.ConfiguracionOperativa.</summary>
public sealed record ConfiguracionOperativa(
    decimal VelocidadKmH, decimal MinutosPorParada, int IntervaloGpsSegundos,
    int RadioValidacionMetros, int MaximoFotos, int MaximoParadas)
{
    public static ConfiguracionOperativa PorDefecto { get; } = new(20, 5, 30, 60, 3, 60);

    /// <summary>Tiempo estimado: viaje por calle a la velocidad configurada más el tiempo de trabajo en cada parada.</summary>
    public double DuracionMinutos(double metros, int paradas) =>
        metros / 1000.0 / (double)VelocidadKmH * 60 + paradas * (double)MinutosPorParada;
}

/// <summary>Usuario activo con rol de cortador o reconectador.</summary>
public sealed record Trabajador(int IdUsuario, string Login, string Nombre, int Pendientes);

/// <summary>Código fijo con lo necesario para planificar y mostrar una parada.</summary>
public sealed record CodigoOperativo(int IdCodigo, int? CodFijo, string? Nombre, byte Estado, double Lon, double Lat)
{
    public Coordenada Ubicacion => new(Lon, Lat);
}

/// <summary>Parada de una asignación con su estado (para mostrar la ruta guardada).</summary>
public sealed record ParadaAsignada(
    int IdDetalle, int Orden, string Estado, CodigoOperativo Codigo,
    double DistanciaTramoMetros, double DistanciaAccesoMetros, IReadOnlyList<Coordenada> Tramo);

/// <summary>Asignación activa (Pendiente o EnCurso) de un trabajador para un tipo de trabajo.</summary>
public sealed record AsignacionActiva(
    int IdAsignacion, TipoAsignacion Tipo, int IdTrabajador, string Estado, DateTime FechaAsignacion,
    Coordenada Partida, double DistanciaMetros, double DuracionEstimadaMin, IReadOnlyList<ParadaAsignada> Paradas)
{
    public IEnumerable<ParadaAsignada> Pendientes => Paradas.Where(p => p.Estado == EstadosParada.Pendiente);
    public int OrdenMaximoRealizado => Paradas.Where(p => p.Estado != EstadosParada.Pendiente).Select(p => p.Orden).DefaultIfEmpty(0).Max();
}

/// <summary>Ruta recalculada que se guarda: los pendientes del trabajador más los códigos nuevos, en el orden óptimo.</summary>
public sealed record PlanAGuardar(
    TipoAsignacion Tipo, int IdTrabajador, int IdAsignadoPor, Coordenada Partida,
    double DistanciaMetros, double DuracionMin, IReadOnlyList<Coordenada> Ruta, IReadOnlyList<ParadaAGuardar> Paradas);

public sealed record ParadaAGuardar(
    int IdCodigo, int Orden, double DistanciaTramo, double DistanciaAcceso, IReadOnlyList<Coordenada> Tramo);

/// <summary>Parada pendiente tal como la ve el trabajador en el celular.</summary>
public sealed record TareaCampo(
    int IdDetalle, int IdAsignacion, TipoAsignacion Tipo, string EstadoAsignacion, int Orden,
    CodigoOperativo Codigo, double DistanciaTramoMetros, double DistanciaAccesoMetros);

/// <summary>Registro de campo ya validado, listo para guardarse en una sola transacción.</summary>
public sealed record EjecucionARegistrar(
    int IdDetalle, int IdAsignacion, int IdCodigo, string Resultado, string EstadoParada,
    string? NumeroMedidor, decimal? Lectura, string? Observacion, DateTime? FechaDispositivo,
    Coordenada Ubicacion, double? PrecisionMetros, double DistanciaAlPuntoMetros, bool DentroDelRadio,
    EstadoServicio? NuevoEstadoCodigo);

public sealed record FotoGuardada(string NombreArchivo, string TipoContenido, int TamanoBytes, string HashSha256);

public sealed record PosicionGps(DateTime FechaHora, double Lat, double Lon, double? PrecisionMetros, double? VelocidadMs)
{
    public Coordenada Ubicacion => new(Lon, Lat);
}

/// <summary>Última posición conocida de un trabajador (monitoreo en vivo).</summary>
public sealed record UltimaPosicion(
    int IdTrabajador, string Login, string Nombre, string Rol, DateTime FechaHora, double Lat, double Lon,
    double? PrecisionMetros, int? IdAsignacion, string? Tipo, int Pendientes, int Realizadas);

/// <summary>Parada ya registrada en campo, con su hora y ubicación.</summary>
public sealed record ParadaRealizada(
    int IdDetalle, int IdAsignacion, TipoAsignacion Tipo, int Orden, int? CodFijo, string? Titular, string Resultado,
    DateTime FechaEjecucion, double Lat, double Lon, double? LatEjecucion, double? LonEjecucion, bool? DentroDelRadio);

/// <summary>Ejecución registrada, para la revisión web (con alerta si se guardó lejos del punto).</summary>
public sealed record EjecucionRegistrada(
    int IdDetalle, int IdAsignacion, TipoAsignacion Tipo, int Orden, string Trabajador,
    int IdCodigo, int? CodFijo, string? Titular, string Resultado, string? NumeroMedidor, decimal? Lectura,
    string? Observacion, DateTime FechaEjecucion, DateTime? FechaDispositivo,
    double LatCodigo, double LonCodigo, double? LatEjecucion, double? LonEjecucion,
    double? PrecisionMetros, double? DistanciaAlPuntoMetros, bool? DentroDelRadio, int Fotos);

public sealed record FiltroEjecuciones(
    TipoAsignacion? Tipo, DateOnly? Desde, DateOnly? Hasta, int? IdTrabajador, bool SoloAlertas, int Pagina, int Tamano);

public sealed record FotoEvidencia(int IdFoto, int IdDetalle, string NombreArchivo, string TipoContenido, int TamanoBytes);

/// <summary>Avance de un trabajador para el dashboard técnico.</summary>
public sealed record AvanceTrabajador(
    int IdTrabajador, string Nombre, string Rol, int Pendientes, int Realizadas, int Observadas,
    int FueraDeRadio, DateTime? UltimaEjecucion);

public sealed record ResumenOperativo(
    int AsignacionesActivas, int Pendientes, int RealizadasHoy, int ObservadasHoy, int FueraDeRadioHoy,
    int CortesPendientes, int ReconexionesPendientes, IReadOnlyList<AvanceTrabajador> Trabajadores);

public interface IOperacionesRepositorio
{
    Task<ConfiguracionOperativa> ObtenerConfiguracionAsync(CancellationToken ct);
    Task GuardarConfiguracionAsync(ConfiguracionOperativa configuracion, CancellationToken ct);

    Task<IReadOnlyList<Trabajador>> ListarTrabajadoresAsync(TipoAsignacion tipo, CancellationToken ct);
    /// <summary>Usuario activo con el rol del tipo de trabajo.</summary>
    Task<bool> EsTrabajadorAsync(int idUsuario, TipoAsignacion tipo, CancellationToken ct);
    /// <summary>Códigos en el estado del tipo («Para Corte» o «Cortado»), con ubicación y sin parada pendiente.</summary>
    Task<IReadOnlyList<CodigoOperativo>> ListarCandidatosAsync(TipoAsignacion tipo, CancellationToken ct);
    Task<AsignacionActiva?> ObtenerActivaAsync(int idTrabajador, TipoAsignacion tipo, CancellationToken ct);
    Task<AsignacionActiva?> ObtenerAsignacionAsync(int idAsignacion, CancellationToken ct);
    /// <summary>Crea la asignación o actualiza la activa del trabajador. Falla si algún código nuevo ya no es candidato.</summary>
    Task<int> GuardarPlanAsync(PlanAGuardar plan, CancellationToken ct);
    Task<bool> AnularAsync(int idAsignacion, CancellationToken ct);

    Task<IReadOnlyList<TareaCampo>> ListarTareasAsync(int idTrabajador, CancellationToken ct);
    /// <summary>Tarea pendiente que pertenece al trabajador; null si no existe, ya se registró o es de otro.</summary>
    Task<TareaCampo?> ObtenerTareaAsync(int idDetalle, int idTrabajador, CancellationToken ct);
    Task<bool> IniciarRecorridoAsync(int idAsignacion, int idTrabajador, CancellationToken ct);
    Task RegistrarEjecucionAsync(EjecucionARegistrar ejecucion, IReadOnlyList<FotoGuardada> fotos, CancellationToken ct);

    Task<bool> AsignacionPerteneceAsync(int idAsignacion, int idTrabajador, CancellationToken ct);
    Task<int> RegistrarPosicionesAsync(int idTrabajador, int? idAsignacion, IReadOnlyList<PosicionGps> posiciones, CancellationToken ct);
    Task<IReadOnlyList<UltimaPosicion>> ListarUltimasPosicionesAsync(DateTime desde, CancellationToken ct);
    Task<IReadOnlyList<(int IdTrabajador, string Nombre, PosicionGps Posicion)>> ListarPosicionesAsync(
        DateOnly fecha, int? idTrabajador, TipoAsignacion? tipo, CancellationToken ct);
    Task<IReadOnlyList<(int IdTrabajador, ParadaRealizada Parada)>> ListarRealizadasAsync(
        DateOnly fecha, int? idTrabajador, TipoAsignacion? tipo, CancellationToken ct);

    Task<Pagina<EjecucionRegistrada>> ListarEjecucionesAsync(FiltroEjecuciones filtro, CancellationToken ct);
    Task<IReadOnlyList<FotoEvidencia>> ListarFotosAsync(int idDetalle, CancellationToken ct);
    Task<FotoEvidencia?> ObtenerFotoAsync(int idFoto, CancellationToken ct);
    Task<ResumenOperativo> ObtenerResumenAsync(DateOnly dia, CancellationToken ct);
}

/// <summary>Fotografías de evidencia en disco (fuera de wwwroot); la base guarda solo los metadatos.</summary>
public interface IAlmacenEvidencias
{
    /// <summary>Valida que sea JPEG o PNG por su contenido, lo guarda con un nombre nuevo y devuelve sus datos.</summary>
    Task<FotoGuardada> GuardarAsync(Stream contenido, CancellationToken ct);
    Stream? Abrir(string nombreArchivo);
    void Eliminar(string nombreArchivo);
}

/// <summary>Red vial de dbo.Vias lista para calcular rutas (se arma una vez y se reutiliza).</summary>
public interface IRedVialProveedor
{
    Task<RedVial> ObtenerAsync(CancellationToken ct);
}
