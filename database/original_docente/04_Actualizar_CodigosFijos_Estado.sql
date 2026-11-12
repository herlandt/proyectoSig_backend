USE VisorDatosSIG;
GO

/*
  Actualización para bases de datos existentes.
  1=Normal, 2=Para Corte, 3=Cortado, 4=Baja Parcial, 5=Baja Total.
*/
IF COL_LENGTH(N'dbo.CodigosFijos', N'Estado') IS NULL
BEGIN
    ALTER TABLE dbo.CodigosFijos
      ADD Estado TINYINT NOT NULL
          CONSTRAINT DF_CodigosFijos_Estado DEFAULT(1) WITH VALUES;
END;
GO

IF COL_LENGTH(N'dbo.CodigosFijos', N'FechaCambioEstado') IS NULL
BEGIN
    ALTER TABLE dbo.CodigosFijos
      ADD FechaCambioEstado DATETIME2 NOT NULL
          CONSTRAINT DF_CodigosFijos_FechaCambioEstado
          DEFAULT(SYSDATETIME()) WITH VALUES;
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
     WHERE name = N'CK_CodigosFijos_Estado'
       AND parent_object_id = OBJECT_ID(N'dbo.CodigosFijos'))
BEGIN
    ALTER TABLE dbo.CodigosFijos WITH CHECK
      ADD CONSTRAINT CK_CodigosFijos_Estado
      CHECK (Estado BETWEEN 1 AND 5);
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
     WHERE name = N'IX_CodigosFijos_Estado'
       AND object_id = OBJECT_ID(N'dbo.CodigosFijos'))
BEGIN
    CREATE INDEX IX_CodigosFijos_Estado ON dbo.CodigosFijos(Estado);
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_CodigosFijos_FechaCambioEstado
ON dbo.CodigosFijos
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT UPDATE(Estado) RETURN;

    UPDATE c
       SET FechaCambioEstado = SYSDATETIME()
      FROM dbo.CodigosFijos c
      JOIN inserted i ON i.IdCodigo = c.IdCodigo
      JOIN deleted d ON d.IdCodigo = i.IdCodigo
     WHERE i.Estado <> d.Estado;
END;
GO

CREATE OR ALTER PROCEDURE dbo.sp_BuscarInmueble
    @Texto NVARCHAR(100)=NULL,
    @UV NVARCHAR(15)=NULL,
    @Mza NVARCHAR(10)=NULL,
    @Lote NVARCHAR(15)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP(100)
        c.IdCodigo, c.CodFijo, c.Nombre, c.Estado, c.FechaCambioEstado,
        m.UV, m.MZA, l.NroLote,
        COALESCE(c.Latitud, c.Geom.STY) AS Latitud,
        COALESCE(c.Longitud, c.Geom.STX) AS Longitud
    FROM dbo.CodigosFijos c
    OUTER APPLY (
        SELECT TOP(1) x.IdLote, x.NroLote, x.IdManzana
        FROM dbo.Lotes x
        WHERE c.Geom IS NOT NULL AND x.Geom IS NOT NULL
          AND x.Geom.STIntersects(c.Geom)=1
    ) l
    LEFT JOIN dbo.Manzanas m ON m.IdManzana=l.IdManzana
    WHERE (@Texto IS NULL OR CONVERT(NVARCHAR(30),c.CodFijo)=@Texto
           OR c.Nombre LIKE N'%' + @Texto + N'%')
      AND (@UV IS NULL OR m.UV=@UV)
      AND (@Mza IS NULL OR m.MZA=@Mza)
      AND (@Lote IS NULL OR l.NroLote=@Lote)
    ORDER BY c.CodFijo;
END;
GO

SELECT Estado,
       CASE Estado
         WHEN 1 THEN N'Normal'
         WHEN 2 THEN N'Para Corte'
         WHEN 3 THEN N'Cortado'
         WHEN 4 THEN N'Baja Parcial'
         WHEN 5 THEN N'Baja Total'
       END AS Descripcion,
       COUNT(*) AS Cantidad
  FROM dbo.CodigosFijos
 GROUP BY Estado
 ORDER BY Estado;
GO
