namespace VisorDatosSIG.Domain;

/// <summary>Trabajo de campo que se asigna a un trabajador (dbo.Asignacion.Tipo).</summary>
public enum TipoAsignacion
{
    Corte,
    Reconexion
}

/// <summary>Reglas de cada tipo de trabajo: a qué rol se asigna, qué códigos entran y en qué estado quedan al ejecutarse.</summary>
public static class TiposAsignacion
{
    public const string RolCortador = "Cortador";
    public const string RolReconexion = "Reconexion";

    /// <summary>Valor guardado en la base (sin tilde, como el CHECK de dbo.Asignacion).</summary>
    public static string Codigo(this TipoAsignacion tipo) => tipo == TipoAsignacion.Corte ? "Corte" : "Reconexion";

    public static string Nombre(this TipoAsignacion tipo) => tipo == TipoAsignacion.Corte ? "Corte" : "Reconexión";

    public static TipoAsignacion? Parsear(string? valor) => valor?.Trim().ToLowerInvariant() switch
    {
        "corte" or "cortes" => TipoAsignacion.Corte,
        "reconexion" or "reconexión" or "reconexiones" => TipoAsignacion.Reconexion,
        _ => null
    };

    /// <summary>Rol de dbo.Roles que puede recibir este trabajo.</summary>
    public static string Rol(this TipoAsignacion tipo) => tipo == TipoAsignacion.Corte ? RolCortador : RolReconexion;

    /// <summary>Estado que debe tener un código para asignarse: «Para Corte» para cortar, «Cortado» para reconectar.</summary>
    public static EstadoServicio EstadoCandidato(this TipoAsignacion tipo) =>
        tipo == TipoAsignacion.Corte ? EstadoServicio.ParaCorte : EstadoServicio.Cortado;

    /// <summary>Estado en que queda el código cuando el trabajo se realiza.</summary>
    public static EstadoServicio EstadoTrasEjecutar(this TipoAsignacion tipo) =>
        tipo == TipoAsignacion.Corte ? EstadoServicio.Cortado : EstadoServicio.Normal;

    /// <summary>Resultado que indica trabajo realizado («Cortado» o «Reconectado»); la alternativa es «Observado».</summary>
    public static string ResultadoRealizado(this TipoAsignacion tipo) =>
        tipo == TipoAsignacion.Corte ? ResultadosEjecucion.Cortado : ResultadosEjecucion.Reconectado;
}

/// <summary>Resultado registrado en campo (dbo.AsignacionDetalle.Resultado).</summary>
public static class ResultadosEjecucion
{
    public const string Cortado = "Cortado";
    public const string Reconectado = "Reconectado";
    public const string Observado = "Observado";
}

/// <summary>Estado de cada parada de la ruta (dbo.AsignacionDetalle.Estado).</summary>
public static class EstadosParada
{
    public const string Pendiente = "Pendiente";
    public const string Ejecutada = "Ejecutada";
    public const string Observada = "Observada";
    public const string Anulada = "Anulada";
}

/// <summary>Estado de la asignación completa (dbo.Asignacion.Estado).</summary>
public static class EstadosAsignacion
{
    public const string Pendiente = "Pendiente";
    public const string EnCurso = "EnCurso";
    public const string Completada = "Completada";
    public const string Anulada = "Anulada";
}
