USE VisorDatosSIG;
GO

/*
  Ejecutar una sola vez en instalaciones existentes.
  La asociación espacial inicial puede tardar aproximadamente 30 segundos.
  Después, el visor consulta la relación Código Fijo -> Lote mediante un índice.
*/

IF COL_LENGTH(N'dbo.CodigosFijos', N'IdLote') IS NULL
    ALTER TABLE dbo.CodigosFijos ADD IdLote INT NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
     WHERE object_id = OBJECT_ID(N'dbo.CodigosFijos')
       AND name = N'IX_CodigosFijos_IdLote'
)
    CREATE INDEX IX_CodigosFijos_IdLote ON dbo.CodigosFijos(IdLote);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
     WHERE parent_object_id = OBJECT_ID(N'dbo.CodigosFijos')
       AND name = N'FK_CodigosFijos_Lotes'
)
    ALTER TABLE dbo.CodigosFijos WITH CHECK
        ADD CONSTRAINT FK_CodigosFijos_Lotes
        FOREIGN KEY(IdLote) REFERENCES dbo.Lotes(IdLote);
GO

CREATE OR ALTER PROCEDURE dbo.sp_ActualizarLoteCodigosFijos
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /* Completa IdManzana cuando la importación SHP dejó el vínculo vacío. */
    UPDATE l
       SET IdManzana = a.IdManzana
      FROM dbo.Lotes l
      CROSS APPLY (
          SELECT TOP (1) m.IdManzana
            FROM dbo.Manzanas m
           WHERE l.Geom IS NOT NULL
             AND m.Geom IS NOT NULL
             AND m.Geom.STSrid = l.Geom.STSrid
             AND m.Geom.Filter(l.Geom.STPointOnSurface()) = 1
             AND m.Geom.STIntersects(l.Geom.STPointOnSurface()) = 1
           ORDER BY m.IdManzana
      ) a
     WHERE l.IdManzana IS NULL;

    UPDATE c
       SET IdLote = a.IdLote
      FROM dbo.CodigosFijos c
      OUTER APPLY (
          SELECT TOP (1) l.IdLote
            FROM dbo.Lotes l
           WHERE c.Geom IS NOT NULL
             AND l.Geom IS NOT NULL
             AND l.Geom.STSrid = c.Geom.STSrid
             AND l.Geom.Filter(c.Geom) = 1
             AND l.Geom.STIntersects(c.Geom) = 1
           ORDER BY l.Geom.STArea(), l.IdLote
      ) a
     WHERE ISNULL(c.IdLote, -1) <> ISNULL(a.IdLote, -1);

    SELECT COUNT(*) AS TotalCodigos,
           COUNT(IdLote) AS AsociadosALote,
           COUNT(*) - COUNT(IdLote) AS SinLote
      FROM dbo.CodigosFijos;

    SELECT COUNT(*) AS TotalLotes,
           COUNT(IdManzana) AS AsociadosAManzana,
           COUNT(*) - COUNT(IdManzana) AS SinManzana
      FROM dbo.Lotes;
END;
GO

EXEC dbo.sp_ActualizarLoteCodigosFijos;
GO

/* Volver a ejecutar el procedimiento después de importar o modificar lotes/puntos:
   EXEC dbo.sp_ActualizarLoteCodigosFijos;
*/
