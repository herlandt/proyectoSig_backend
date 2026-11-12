namespace VisorDatosSIG.Domain;

/// <summary>Estado del servicio de agua de un código fijo (CK_CodigosFijos_Estado: 1..5).</summary>
public enum EstadoServicio : byte
{
    Normal = 1,
    ParaCorte = 2,
    Cortado = 3,
    BajaParcial = 4,
    BajaTotal = 5
}

public static class EstadoServicioExtensiones
{
    public static string Descripcion(this EstadoServicio estado) => estado switch
    {
        EstadoServicio.Normal => "Normal",
        EstadoServicio.ParaCorte => "Para Corte",
        EstadoServicio.Cortado => "Cortado",
        EstadoServicio.BajaParcial => "Baja Parcial",
        EstadoServicio.BajaTotal => "Baja Total",
        _ => "Desconocido"
    };

    public static string Descripcion(int estado) =>
        Enum.IsDefined(typeof(EstadoServicio), (byte)estado) ? ((EstadoServicio)estado).Descripcion() : "Desconocido";
}
