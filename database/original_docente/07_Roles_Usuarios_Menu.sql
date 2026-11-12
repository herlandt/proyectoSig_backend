USE VisorDatosSIG;
GO

IF OBJECT_ID(N'dbo.Roles',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Roles(
        IdRol INT IDENTITY(1,1) PRIMARY KEY,
        NombreRol VARCHAR(50) NOT NULL UNIQUE,
        Descripcion VARCHAR(200) NULL,
        Estado BIT NOT NULL CONSTRAINT DF_Roles_Estado DEFAULT(1)
    );
END;
GO

IF OBJECT_ID(N'dbo.UsuariosRoles',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UsuariosRoles(
        IdUsuarioRol INT IDENTITY(1,1) PRIMARY KEY,
        IdUsuario INT NOT NULL,
        IdRol INT NOT NULL,
        CONSTRAINT FK_UsuariosRoles_Usuarios FOREIGN KEY(IdUsuario)
            REFERENCES dbo.Usuarios(IdUsuario),
        CONSTRAINT FK_UsuariosRoles_Roles FOREIGN KEY(IdRol)
            REFERENCES dbo.Roles(IdRol),
        CONSTRAINT UQ_UsuariosRoles UNIQUE(IdUsuario,IdRol)
    );
END;
GO

MERGE dbo.Roles AS destino
USING (VALUES
 ('Administrador','Administrador del Sistema'),
 ('Catastro','Rol para crear, modificar y eliminar Manzanas, Lotes, Codigo Fijo y Vias'),
 ('Lecturador','Rol para lecturar medidores'),
 ('Cortador','Rol para cortar servicios'),
 ('Reconexion','Rol para la reconnexion por corte')
) AS origen(NombreRol,Descripcion)
ON destino.NombreRol=origen.NombreRol
WHEN MATCHED THEN UPDATE SET Descripcion=origen.Descripcion,Estado=1
WHEN NOT MATCHED THEN INSERT(NombreRol,Descripcion,Estado)
VALUES(origen.NombreRol,origen.Descripcion,1);
GO

IF NOT EXISTS(SELECT 1 FROM dbo.Usuarios WHERE Login=N'Juan')
BEGIN
    INSERT dbo.Usuarios
        (Login,Nombre,PasswordHash,PasswordSalt,Iteraciones,Activo)
    VALUES
        (N'Juan',N'Juan',
         0xB14BC9A077C4CC29BC030A25AD517456D88224BBB57ECA2C5D977C4E85131676,
         0x3F2B95B127A4E8C161A840BDF72EA6623E318F7969B3CCF7A7B66BA33A90D89C,
         100000,1);
END;

IF NOT EXISTS(SELECT 1 FROM dbo.Usuarios WHERE Login=N'Pedro')
BEGIN
    INSERT dbo.Usuarios
        (Login,Nombre,PasswordHash,PasswordSalt,Iteraciones,Activo)
    VALUES
        (N'Pedro',N'Pedro',
         0xC1BA4D699543F1E1D12C302CD0FD1BB4DDECF6A9C55620B9CC718E918930A528,
         0xA4E75D92036F1B847CD4298A18D3F16B2746AE50A81683CD375943EE15F7BD01,
         100000,1);
END;
GO

INSERT dbo.UsuariosRoles(IdUsuario,IdRol)
SELECT u.IdUsuario,r.IdRol
FROM dbo.Usuarios u
CROSS JOIN dbo.Roles r
WHERE ((u.Login=N'admin' AND r.NombreRol='Administrador')
    OR (u.Login=N'Juan' AND r.NombreRol='Lecturador')
    OR (u.Login=N'Pedro' AND r.NombreRol='Cortador'))
  AND NOT EXISTS(
      SELECT 1 FROM dbo.UsuariosRoles ur
      WHERE ur.IdUsuario=u.IdUsuario AND ur.IdRol=r.IdRol
  );
GO

SELECT u.Login,u.Nombre,r.NombreRol,r.Descripcion
FROM dbo.Usuarios u
LEFT JOIN dbo.UsuariosRoles ur ON ur.IdUsuario=u.IdUsuario
LEFT JOIN dbo.Roles r ON r.IdRol=ur.IdRol
WHERE u.Login IN(N'admin',N'Juan',N'Pedro')
ORDER BY u.Login,r.NombreRol;
GO
