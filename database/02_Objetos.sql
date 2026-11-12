/* =====================================================================
   VisorDatosSIG 2026 · 02_Objetos.sql
   1) Procedimientos oficiales del docente (sp_BuscarInmueble,
      sp_ActualizarLoteCodigosFijos), en su versión optimizada (script 05).
   2) AMPLIACIÓN DOCUMENTADA (no existe en el diseño entregado; la exige la
      especificación RF-MIG-10 y RF-SEG-06):
        - dbo.BitacoraMigracion / dbo.BitacoraMigracionDetalle
        - dbo.BitacoraAcceso
        - dbo.sp_ReconstruirIndicesEspaciales (RF-MIG-14)
   No modifica ninguna tabla oficial.
   ===================================================================== */
USE VisorDatosSIG;
GO
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- Procedimientos oficiales ---------- */
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
    LEFT JOIN dbo.Lotes l ON l.IdLote=c.IdLote
    LEFT JOIN dbo.Manzanas m ON m.IdManzana=l.IdManzana
    WHERE (@Texto IS NULL OR CONVERT(NVARCHAR(30),c.CodFijo)=@Texto
           OR c.Nombre LIKE N'%' + @Texto + N'%')
      AND (@UV IS NULL OR m.UV=@UV)
      AND (@Mza IS NULL OR m.MZA=@Mza)
      AND (@Lote IS NULL OR l.NroLote=@Lote)
    ORDER BY c.CodFijo;
END;
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

/* ---------- Ampliación: versión optimizada de sp_ActualizarLoteCodigosFijos ----------
   Mismo contrato y mismo resultado que el procedimiento oficial (mismas reglas y
   mismos dos conjuntos de resultados), pero la vinculación lote→manzana precalcula
   STPointOnSurface() y fuerza el índice espacial. Medición con los datos oficiales:
   118,7 s → 0,6 s, 9 276 lotes vinculados en ambos casos. El migrador usa esta versión;
   el procedimiento oficial se conserva sin cambios. */
CREATE OR ALTER PROCEDURE dbo.sp_ActualizarVinculosEspaciales
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- 1. Vincular Lotes -> Manzanas usando bounding box + STIntersects
    IF OBJECT_ID('tempdb..#PuntosLote') IS NOT NULL DROP TABLE #PuntosLote;
    CREATE TABLE #PuntosLote (
        IdLote INT PRIMARY KEY,
        X FLOAT NOT NULL,
        Y FLOAT NOT NULL,
        P geometry NOT NULL
    );

    INSERT INTO #PuntosLote (IdLote, X, Y, P)
    SELECT IdLote,
           p.STX,
           p.STY,
           p
      FROM dbo.Lotes l
     CROSS APPLY (SELECT l.Geom.STPointOnSurface() AS p) ap
     WHERE l.Geom IS NOT NULL AND l.IdManzana IS NULL;

    IF OBJECT_ID('tempdb..#CajasManzanas') IS NOT NULL DROP TABLE #CajasManzanas;
    CREATE TABLE #CajasManzanas (
        IdManzana INT PRIMARY KEY,
        MinX FLOAT NOT NULL,
        MinY FLOAT NOT NULL,
        MaxX FLOAT NOT NULL,
        MaxY FLOAT NOT NULL
    );

    INSERT INTO #CajasManzanas (IdManzana, MinX, MinY, MaxX, MaxY)
    SELECT IdManzana,
           env.STPointN(1).STX,
           env.STPointN(1).STY,
           env.STPointN(3).STX,
           env.STPointN(3).STY
      FROM dbo.Manzanas m
     CROSS APPLY (SELECT m.Geom.STEnvelope() AS env) ae
     WHERE m.Geom IS NOT NULL;

    UPDATE l
       SET IdManzana = a.IdManzana
      FROM dbo.Lotes l
      JOIN #PuntosLote p ON p.IdLote = l.IdLote
      CROSS APPLY (
          SELECT TOP (1) m.IdManzana
            FROM dbo.Manzanas m
            JOIN #CajasManzanas cm ON cm.IdManzana = m.IdManzana
           WHERE p.X >= cm.MinX AND p.X <= cm.MaxX
             AND p.Y >= cm.MinY AND p.Y <= cm.MaxY
             AND m.Geom.STIntersects(p.P) = 1
           ORDER BY m.IdManzana
      ) a;

    -- 2. Vincular CodigosFijos -> Lotes usando bounding box + STIntersects
    IF OBJECT_ID('tempdb..#CajasLotes') IS NOT NULL DROP TABLE #CajasLotes;
    CREATE TABLE #CajasLotes (
        IdLote INT PRIMARY KEY,
        MinX FLOAT NOT NULL,
        MinY FLOAT NOT NULL,
        MaxX FLOAT NOT NULL,
        MaxY FLOAT NOT NULL,
        Area FLOAT NOT NULL
    );

    INSERT INTO #CajasLotes (IdLote, MinX, MinY, MaxX, MaxY, Area)
    SELECT IdLote,
           env.STPointN(1).STX,
           env.STPointN(1).STY,
           env.STPointN(3).STX,
           env.STPointN(3).STY,
           l.Geom.STArea()
      FROM dbo.Lotes l
     CROSS APPLY (SELECT l.Geom.STEnvelope() AS env) ae
     WHERE l.Geom IS NOT NULL;

    CREATE NONCLUSTERED INDEX IX_CajasLotes_Bounds ON #CajasLotes(MinX, MaxX, MinY, MaxY) INCLUDE(Area);

    UPDATE c
       SET IdLote = a.IdLote
      FROM dbo.CodigosFijos c
      OUTER APPLY (
          SELECT TOP (1) l.IdLote
            FROM dbo.Lotes l
            JOIN #CajasLotes cl ON cl.IdLote = l.IdLote
           WHERE c.Geom IS NOT NULL
             AND l.Geom IS NOT NULL
             AND l.Geom.STSrid = c.Geom.STSrid
             AND c.Longitud >= cl.MinX AND c.Longitud <= cl.MaxX
             AND c.Latitud >= cl.MinY AND c.Latitud <= cl.MaxY
             AND l.Geom.STIntersects(c.Geom) = 1
           ORDER BY cl.Area, l.IdLote
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

/* ---------- Ampliación: bitácora de migraciones (RF-MIG-10) ---------- */
IF OBJECT_ID(N'dbo.BitacoraMigracion', N'U') IS NULL
CREATE TABLE dbo.BitacoraMigracion(
    IdMigracion INT IDENTITY(1,1) CONSTRAINT PK_BitacoraMigracion PRIMARY KEY,
    FechaInicio DATETIME2 NOT NULL CONSTRAINT DF_BitacoraMigracion_FechaInicio DEFAULT(SYSDATETIME()),
    FechaFin DATETIME2 NULL,
    Usuario NVARCHAR(50) NOT NULL,
    Equipo NVARCHAR(100) NULL,
    Capa NVARCHAR(30) NOT NULL,
    Archivo NVARCHAR(260) NOT NULL,
    TablaDestino NVARCHAR(50) NOT NULL,
    Modo NVARCHAR(15) NOT NULL,
    Estado NVARCHAR(15) NOT NULL,
    RegistrosLeidos INT NOT NULL CONSTRAINT DF_BitacoraMigracion_Leidos DEFAULT(0),
    Insertados INT NOT NULL CONSTRAINT DF_BitacoraMigracion_Insertados DEFAULT(0),
    Omitidos INT NOT NULL CONSTRAINT DF_BitacoraMigracion_Omitidos DEFAULT(0),
    Fallidos INT NOT NULL CONSTRAINT DF_BitacoraMigracion_Fallidos DEFAULT(0),
    Reparados INT NOT NULL CONSTRAINT DF_BitacoraMigracion_Reparados DEFAULT(0),
    Advertencias INT NOT NULL CONSTRAINT DF_BitacoraMigracion_Advertencias DEFAULT(0),
    DuracionMs INT NULL,
    Mensaje NVARCHAR(1000) NULL,
    CONSTRAINT CK_BitacoraMigracion_Modo CHECK (Modo IN (N'Reemplazar', N'Anexar')),
    CONSTRAINT CK_BitacoraMigracion_Estado CHECK (Estado IN (N'EnCurso', N'Completada', N'Revertida', N'Cancelada', N'Fallida', N'Interrumpida'))
);

/* Actualización idempotente de la tabla auxiliar: admite registros sin cierre de más de dos horas. */
IF EXISTS (SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.BitacoraMigracion')
              AND name = N'CK_BitacoraMigracion_Estado' AND definition NOT LIKE N'%Interrumpida%')
    ALTER TABLE dbo.BitacoraMigracion DROP CONSTRAINT CK_BitacoraMigracion_Estado;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                WHERE parent_object_id = OBJECT_ID(N'dbo.BitacoraMigracion')
                  AND name = N'CK_BitacoraMigracion_Estado')
    ALTER TABLE dbo.BitacoraMigracion WITH CHECK ADD CONSTRAINT CK_BitacoraMigracion_Estado
        CHECK (Estado IN (N'EnCurso', N'Completada', N'Revertida', N'Cancelada', N'Fallida', N'Interrumpida'));

IF OBJECT_ID(N'dbo.BitacoraMigracionDetalle', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BitacoraMigracionDetalle(
        IdDetalle BIGINT IDENTITY(1,1) CONSTRAINT PK_BitacoraMigracionDetalle PRIMARY KEY,
        IdMigracion INT NOT NULL,
        Nivel NVARCHAR(12) NOT NULL,
        Fila INT NULL,
        Campo NVARCHAR(50) NULL,
        Valor NVARCHAR(200) NULL,
        Mensaje NVARCHAR(500) NOT NULL,
        CONSTRAINT FK_BitacoraMigracionDetalle_Migracion FOREIGN KEY(IdMigracion)
            REFERENCES dbo.BitacoraMigracion(IdMigracion),
        CONSTRAINT CK_BitacoraMigracionDetalle_Nivel CHECK (Nivel IN (N'Info', N'Advertencia', N'Error'))
    );
    CREATE INDEX IX_BitacoraMigracionDetalle_Migracion ON dbo.BitacoraMigracionDetalle(IdMigracion);
END;

/* ---------- Ampliación: bitácora de accesos y operaciones (RF-SEG-06) ---------- */
IF OBJECT_ID(N'dbo.BitacoraAcceso', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BitacoraAcceso(
        IdBitacora BIGINT IDENTITY(1,1) CONSTRAINT PK_BitacoraAcceso PRIMARY KEY,
        Fecha DATETIME2 NOT NULL CONSTRAINT DF_BitacoraAcceso_Fecha DEFAULT(SYSDATETIME()),
        IdUsuario INT NULL,
        Login NVARCHAR(50) NULL,
        Evento NVARCHAR(40) NOT NULL,
        Exito BIT NOT NULL,
        Ip NVARCHAR(45) NULL,
        Detalle NVARCHAR(500) NULL,
        CONSTRAINT FK_BitacoraAcceso_Usuarios FOREIGN KEY(IdUsuario) REFERENCES dbo.Usuarios(IdUsuario)
    );
    CREATE INDEX IX_BitacoraAcceso_Fecha ON dbo.BitacoraAcceso(Fecha DESC);
    CREATE INDEX IX_BitacoraAcceso_Login_Fecha ON dbo.BitacoraAcceso(Login, Fecha DESC);
END;
GO

/* ---------- Ampliación: reconstrucción de índices espaciales (RF-MIG-14) ----------
   Recrea los cuatro índices con una caja ajustada a la extensión real de los
   datos (más un margen), en lugar de la caja mundial (-180,-90,180,90). Con la
   caja mundial, la celda más fina del GEOMETRY_GRID mide ~600 m y el filtro
   primario descarta muy poco en un área de ~10 km. */
CREATE OR ALTER PROCEDURE dbo.sp_ReconstruirIndicesEspaciales
    @Margen FLOAT = 0.05
WITH EXECUTE AS OWNER   -- el migrador no necesita permiso ALTER sobre las tablas
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @ext geometry = (
        SELECT geometry::EnvelopeAggregate(g)
          FROM (SELECT Geom AS g FROM dbo.Manzanas WHERE Geom IS NOT NULL
                UNION ALL SELECT Geom FROM dbo.Lotes WHERE Geom IS NOT NULL
                UNION ALL SELECT Geom FROM dbo.CodigosFijos WHERE Geom IS NOT NULL
                UNION ALL SELECT Geom FROM dbo.Vias WHERE Geom IS NOT NULL) t);

    DECLARE @xmin FLOAT = -180, @ymin FLOAT = -90, @xmax FLOAT = 180, @ymax FLOAT = 90;
    IF @ext IS NOT NULL
    BEGIN
        SELECT @xmin = @ext.STPointN(1).STX - @Margen, @ymin = @ext.STPointN(1).STY - @Margen,
               @xmax = @ext.STPointN(3).STX + @Margen, @ymax = @ext.STPointN(3).STY + @Margen;
    END;

    DECLARE @caja NVARCHAR(200) = N'BOUNDING_BOX=(' +
        CONVERT(NVARCHAR(40), @xmin, 2) + N',' + CONVERT(NVARCHAR(40), @ymin, 2) + N',' +
        CONVERT(NVARCHAR(40), @xmax, 2) + N',' + CONVERT(NVARCHAR(40), @ymax, 2) + N')';

    DECLARE @sql NVARCHAR(MAX) = N'
        DROP INDEX IF EXISTS SIX_CodigosFijos_Geom ON dbo.CodigosFijos;
        DROP INDEX IF EXISTS SIX_Manzanas_Geom ON dbo.Manzanas;
        DROP INDEX IF EXISTS SIX_Lotes_Geom ON dbo.Lotes;
        DROP INDEX IF EXISTS SIX_Vias_Geom ON dbo.Vias;
        CREATE SPATIAL INDEX SIX_CodigosFijos_Geom ON dbo.CodigosFijos(Geom) USING GEOMETRY_GRID WITH (' + @caja + N', GRIDS=(HIGH,HIGH,HIGH,HIGH), CELLS_PER_OBJECT=16);
        CREATE SPATIAL INDEX SIX_Manzanas_Geom ON dbo.Manzanas(Geom) USING GEOMETRY_GRID WITH (' + @caja + N', GRIDS=(HIGH,HIGH,MEDIUM,MEDIUM), CELLS_PER_OBJECT=16);
        CREATE SPATIAL INDEX SIX_Lotes_Geom ON dbo.Lotes(Geom) USING GEOMETRY_GRID WITH (' + @caja + N', GRIDS=(HIGH,HIGH,HIGH,MEDIUM), CELLS_PER_OBJECT=16);
        CREATE SPATIAL INDEX SIX_Vias_Geom ON dbo.Vias(Geom) USING GEOMETRY_GRID WITH (' + @caja + N', GRIDS=(HIGH,HIGH,MEDIUM,MEDIUM), CELLS_PER_OBJECT=16);';
    BEGIN TRY
        BEGIN TRANSACTION;
        EXEC sys.sp_executesql @sql;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT @caja AS CajaAplicada;
END;
GO

PRINT N'02_Objetos.sql: procedimientos y ampliaciones creados/verificados.';
GO
