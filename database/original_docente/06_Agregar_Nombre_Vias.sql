USE VisorDatosSIG;
GO

IF COL_LENGTH(N'dbo.Vias', N'Nombre') IS NULL
BEGIN
    ALTER TABLE dbo.Vias
        ADD Nombre NVARCHAR(40) NULL;
END;
GO

SELECT COUNT(*) AS TotalVias,
       COUNT(Nombre) AS ViasConNombre,
       COUNT(*) - COUNT(Nombre) AS ViasSinNombre
FROM dbo.Vias;
GO
