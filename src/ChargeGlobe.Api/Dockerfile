# Build context: repository root
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props ./
COPY src/ChargeGlobe.Domain/ChargeGlobe.Domain.csproj src/ChargeGlobe.Domain/
COPY src/ChargeGlobe.Application/ChargeGlobe.Application.csproj src/ChargeGlobe.Application/
COPY src/ChargeGlobe.Infrastructure/ChargeGlobe.Infrastructure.csproj src/ChargeGlobe.Infrastructure/
COPY src/ChargeGlobe.Api/ChargeGlobe.Api.csproj src/ChargeGlobe.Api/
RUN dotnet restore src/ChargeGlobe.Api/ChargeGlobe.Api.csproj
COPY src/ChargeGlobe.Domain/ src/ChargeGlobe.Domain/
COPY src/ChargeGlobe.Application/ src/ChargeGlobe.Application/
COPY src/ChargeGlobe.Infrastructure/ src/ChargeGlobe.Infrastructure/
COPY src/ChargeGlobe.Api/ src/ChargeGlobe.Api/
RUN dotnet publish src/ChargeGlobe.Api/ChargeGlobe.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ChargeGlobe.Api.dll"]
