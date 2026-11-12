FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar csproj y restaurar dependencias
COPY src/VisorDatosSIG.Domain/VisorDatosSIG.Domain.csproj src/VisorDatosSIG.Domain/
COPY src/VisorDatosSIG.Application/VisorDatosSIG.Application.csproj src/VisorDatosSIG.Application/
COPY src/VisorDatosSIG.Infrastructure/VisorDatosSIG.Infrastructure.csproj src/VisorDatosSIG.Infrastructure/
COPY src/VisorDatosSIG.Api/VisorDatosSIG.Api.csproj src/VisorDatosSIG.Api/
RUN dotnet restore src/VisorDatosSIG.Api/VisorDatosSIG.Api.csproj

# Compilar
COPY src/ src/
RUN dotnet publish src/VisorDatosSIG.Api/VisorDatosSIG.Api.csproj -c Release -o /app/publish --no-restore

# Imagen final
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
COPY claves/ /app/claves/

# Render inyecta PORT
ENV ASPNETCORE_URLS=http://+:${PORT:-10000}
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 10000
ENTRYPOINT ["dotnet", "VisorDatosSIG.Api.dll"]

