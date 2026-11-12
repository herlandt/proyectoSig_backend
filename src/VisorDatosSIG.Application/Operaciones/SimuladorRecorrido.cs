using VisorDatosSIG.Application.Rutas;
using VisorDatosSIG.Domain;

namespace VisorDatosSIG.Application.Operaciones;

/// <summary>
/// Recorrido simulado de un trabajador sobre su ruta asignada, para demostrar el monitoreo sin un celular en
/// San Ignacio: avanza por los tramos a la velocidad configurada, se detiene en cada parada los minutos
/// configurados y genera una posición por intervalo de GPS, con un error de pocos metros reproducible.
/// </summary>
public static class SimuladorRecorrido
{
    public static IReadOnlyList<PosicionGps> Generar(
        AsignacionActiva asignacion, ConfiguracionOperativa configuracion, DateTime inicio, int? maximoParadas = null, int semilla = 442)
    {
        double velocidad = (double)configuracion.VelocidadKmH / 3.6;   // m/s
        double intervalo = Math.Max(1, configuracion.IntervaloGpsSegundos);
        double pausa = (double)configuracion.MinutosPorParada * 60;
        var aleatorio = new Random(semilla);
        var posiciones = new List<PosicionGps>();
        double reloj = 0;       // segundos desde el inicio
        double muestra = 0;     // instante de la próxima posición

        void Registrar(Coordenada c, double segundos, double velocidadMs)
        {
            // Error típico de un GPS de celular a cielo abierto (2 a 6 m en una dirección al azar).
            double error = 2 + aleatorio.NextDouble() * 4, rumbo = aleatorio.NextDouble() * 2 * Math.PI;
            double lat = c.Lat + error * Math.Cos(rumbo) / 111_320;
            double lon = c.Lon + error * Math.Sin(rumbo) / (111_320 * Math.Cos(c.Lat * Math.PI / 180));
            posiciones.Add(new PosicionGps(inicio.AddSeconds(segundos), Math.Round(lat, 7), Math.Round(lon, 7),
                Math.Round(4 + aleatorio.NextDouble() * 8, 1), Math.Round(velocidadMs, 2)));
        }

        var anterior = asignacion.Partida;
        var paradas = asignacion.Paradas.Where(p => p.Estado == EstadosParada.Pendiente).OrderBy(p => p.Orden);
        foreach (var parada in paradas.Take(maximoParadas ?? int.MaxValue))
        {
            // El tramo va de la parada anterior a esta por las calles; sin tramo se camina en línea recta.
            var camino = new List<Coordenada> { anterior };
            camino.AddRange(parada.Tramo);
            camino.Add(parada.Codigo.Ubicacion);
            for (int i = 1; i < camino.Count; i++)
            {
                double largo = RedVial.Distancia(camino[i - 1], camino[i]);
                if (largo < 0.01) continue;
                double fin = reloj + largo / velocidad;
                for (; muestra <= fin; muestra += intervalo)
                {
                    double f = (muestra - reloj) / (fin - reloj);
                    Registrar(new Coordenada(camino[i - 1].Lon + (camino[i].Lon - camino[i - 1].Lon) * f,
                        camino[i - 1].Lat + (camino[i].Lat - camino[i - 1].Lat) * f), muestra, velocidad);
                }
                reloj = fin;
            }
            // Detenido en la parada mientras registra el trabajo.
            for (reloj += pausa; muestra <= reloj; muestra += intervalo)
                Registrar(parada.Codigo.Ubicacion, muestra, 0);
            anterior = parada.Codigo.Ubicacion;
        }
        return posiciones;
    }
}
