using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using VisorDatosSIG.Infrastructure.Datos;

namespace VisorDatosSIG.Api.Controllers;

[ApiController]
[Route("api/salud/bd")]
[AllowAnonymous]
public sealed class SaludController(FabricaConexion fabrica) : ControllerBase
{
    /// <summary>Comprueba SQL Server sin exponer la conexión ni los detalles del error.</summary>
    [HttpGet]
    public async Task<IActionResult> BaseDatos(CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var cn = fabrica.Crear();
            await cn.OpenAsync(limite.Token);
            await using var comando = cn.CreateCommand();
            comando.CommandText = "SELECT 1";
            comando.CommandTimeout = 2;
            await comando.ExecuteScalarAsync(limite.Token);
            return Ok(new { estado = "ok" });
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { estado = "no disponible" });
        }
    }
}
