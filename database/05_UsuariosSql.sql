/* =====================================================================
   VisorDatosSIG 2026 · 05_UsuariosSql.sql   (OPCIONAL · producción)
   Crea los logins SQL de mínimo privilegio y los asocia a los roles de
   03_Seguridad.sql. Las contraseñas NO están en el script: se pasan como
   variables de sqlcmd al ejecutarlo:

   sqlcmd -S SERVIDOR -E -i 05_UsuariosSql.sql ^
          -v PasswordWeb="<contraseña-fuerte>" PasswordMigrador="<contraseña-fuerte>"

   En desarrollo se puede usar autenticación integrada de Windows y omitir
   este script.
   ===================================================================== */
USE master;
GO
IF SUSER_ID(N'visor_web') IS NULL
    CREATE LOGIN visor_web WITH PASSWORD = N'$(PasswordWeb)', CHECK_POLICY = ON;
IF SUSER_ID(N'visor_migrador') IS NULL
    CREATE LOGIN visor_migrador WITH PASSWORD = N'$(PasswordMigrador)', CHECK_POLICY = ON;
GO
USE VisorDatosSIG;
GO
IF DATABASE_PRINCIPAL_ID(N'visor_web') IS NULL CREATE USER visor_web FOR LOGIN visor_web;
IF DATABASE_PRINCIPAL_ID(N'visor_migrador') IS NULL CREATE USER visor_migrador FOR LOGIN visor_migrador;
ALTER ROLE rol_visor_web ADD MEMBER visor_web;
ALTER ROLE rol_visor_migrador ADD MEMBER visor_migrador;
GO
