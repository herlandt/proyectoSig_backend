/* =====================================================================
   VisorDatosSIG 2026 · 06_Verificacion.sql
   Consultas de verificación del Anexo C de la especificación.
   Resultado esperado: conteos iguales a los "válidos" del migrador y
   cero en las verificaciones 2–7. Las secciones 8–9 son informativas.
   ===================================================================== */
USE VisorDatosSIG;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;

PRINT N'1. Conteo por tabla';
SELECT 'Manzanas' AS Tabla, COUNT(*) AS Registros FROM dbo.Manzanas
UNION ALL SELECT 'Lotes', COUNT(*) FROM dbo.Lotes
UNION ALL SELECT 'CodigosFijos', COUNT(*) FROM dbo.CodigosFijos
UNION ALL SELECT 'Vias', COUNT(*) FROM dbo.Vias;

PRINT N'2. Geometrías nulas (esperado 0)';
SELECT 'Manzanas' AS Tabla, COUNT(*) AS Nulas FROM dbo.Manzanas WHERE Geom IS NULL
UNION ALL SELECT 'Lotes', COUNT(*) FROM dbo.Lotes WHERE Geom IS NULL
UNION ALL SELECT 'CodigosFijos', COUNT(*) FROM dbo.CodigosFijos WHERE Geom IS NULL
UNION ALL SELECT 'Vias', COUNT(*) FROM dbo.Vias WHERE Geom IS NULL;

PRINT N'3. SRID distinto de 4326 (esperado 0)';
SELECT 'Manzanas' AS Tabla, COUNT(*) AS SridIncorrecto FROM dbo.Manzanas WHERE Geom.STSrid <> 4326
UNION ALL SELECT 'Lotes', COUNT(*) FROM dbo.Lotes WHERE Geom.STSrid <> 4326
UNION ALL SELECT 'CodigosFijos', COUNT(*) FROM dbo.CodigosFijos WHERE Geom.STSrid <> 4326
UNION ALL SELECT 'Vias', COUNT(*) FROM dbo.Vias WHERE Geom.STSrid <> 4326;

PRINT N'4. Geometrías inválidas (esperado 0)';
SELECT 'Manzanas' AS Tabla, COUNT(*) AS Invalidas FROM dbo.Manzanas WHERE Geom.STIsValid() = 0
UNION ALL SELECT 'Lotes', COUNT(*) FROM dbo.Lotes WHERE Geom.STIsValid() = 0
UNION ALL SELECT 'CodigosFijos', COUNT(*) FROM dbo.CodigosFijos WHERE Geom.STIsValid() = 0
UNION ALL SELECT 'Vias', COUNT(*) FROM dbo.Vias WHERE Geom.STIsValid() = 0;

PRINT N'5. Duplicados por clave de carga (esperado 0)';
SELECT 'Manzanas.IdOrigen' AS Clave, COUNT(*) AS Duplicados FROM
  (SELECT IdOrigen FROM dbo.Manzanas GROUP BY IdOrigen HAVING COUNT(*) > 1) d
UNION ALL SELECT 'Lotes.IdOrigen', COUNT(*) FROM
  (SELECT IdOrigen FROM dbo.Lotes GROUP BY IdOrigen HAVING COUNT(*) > 1) d
UNION ALL SELECT 'CodigosFijos.CodF_SQL+posición', COUNT(*) FROM
  (SELECT CodF_SQL, ROUND(Longitud, 6) AS X, ROUND(Latitud, 6) AS Y FROM dbo.CodigosFijos
    GROUP BY CodF_SQL, ROUND(Longitud, 6), ROUND(Latitud, 6) HAVING COUNT(*) > 1) d
UNION ALL SELECT 'Vias.OBJECTID', COUNT(*) FROM
  (SELECT OBJECTID FROM dbo.Vias GROUP BY OBJECTID HAVING COUNT(*) > 1) d;

PRINT N'6. Claves foráneas huérfanas (esperado 0)';
SELECT 'Lotes sin manzana existente' AS Verificacion, COUNT(*) AS Huerfanos
  FROM dbo.Lotes l WHERE l.IdManzana IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.Manzanas m WHERE m.IdManzana = l.IdManzana)
UNION ALL SELECT 'Códigos sin lote existente', COUNT(*)
  FROM dbo.CodigosFijos c WHERE c.IdLote IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.Lotes l WHERE l.IdLote = c.IdLote);

PRINT N'7. Coherencia de coordenadas de códigos fijos (esperado 0)';
SELECT COUNT(*) AS CoordenadasIncoherentes
  FROM dbo.CodigosFijos
 WHERE ABS(Longitud - Geom.STX) > 1e-9 OR ABS(Latitud - Geom.STY) > 1e-9;

PRINT N'8. Extensión espacial por capa';
SELECT 'Manzanas' AS Tabla, geometry::EnvelopeAggregate(Geom).ToString() AS Extension FROM dbo.Manzanas
UNION ALL SELECT 'Lotes', geometry::EnvelopeAggregate(Geom).ToString() FROM dbo.Lotes
UNION ALL SELECT 'CodigosFijos', geometry::EnvelopeAggregate(Geom).ToString() FROM dbo.CodigosFijos
UNION ALL SELECT 'Vias', geometry::EnvelopeAggregate(Geom).ToString() FROM dbo.Vias;

PRINT N'9. Asociaciones pendientes (informativo; no implica error ni cambia la regla espacial)';
SELECT 'Lotes sin manzana' AS AsociacionPendiente, COUNT(*) AS Registros FROM dbo.Lotes WHERE IdManzana IS NULL
UNION ALL SELECT 'Códigos fijos sin lote', COUNT(*) FROM dbo.CodigosFijos WHERE IdLote IS NULL;
GO
