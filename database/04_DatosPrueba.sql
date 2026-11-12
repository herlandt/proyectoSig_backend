/* =====================================================================
   VisorDatosSIG 2026 · 04_DatosPrueba.sql
   Ejecutar DESPUÉS de migrar las cuatro capas con el migrador.

   Los datos oficiales llegan con Estado = 1 (Normal) en todos los códigos
   fijos. Para demostrar la simbología por estado del visor, este script
   asigna estados de forma determinista (siempre el mismo resultado):
     1 = Normal, 2 = Para Corte, 3 = Cortado, 4 = Baja Parcial, 5 = Baja Total.
   SOLO PARA DEMOSTRACIÓN. No ejecutar sobre datos productivos.
   ===================================================================== */
USE VisorDatosSIG;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.CodigosFijos)
BEGIN
    RAISERROR(N'No hay códigos fijos: migre primero las capas con el migrador.', 16, 1);
    RETURN;
END;

UPDATE dbo.CodigosFijos
   SET Estado = CASE
        WHEN CodFijo % 23 = 0 THEN 2   -- Para Corte
        WHEN CodFijo % 29 = 0 THEN 3   -- Cortado
        WHEN CodFijo % 41 = 0 THEN 4   -- Baja Parcial
        WHEN CodFijo % 67 = 0 THEN 5   -- Baja Total
        ELSE 1 END
 WHERE CodFijo IS NOT NULL;
GO

SELECT Estado,
       CASE Estado WHEN 1 THEN N'Normal' WHEN 2 THEN N'Para Corte' WHEN 3 THEN N'Cortado'
                   WHEN 4 THEN N'Baja Parcial' WHEN 5 THEN N'Baja Total' END AS Descripcion,
       COUNT(*) AS Cantidad
  FROM dbo.CodigosFijos
 GROUP BY Estado
 ORDER BY Estado;
GO
