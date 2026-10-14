# Build context: repository root. One image for both roles: --role crawler|worker
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props ./
COPY src/ChargeGlobe.Domain/ChargeGlobe.Domain.csproj src/ChargeGlobe.Domain/
COPY src/ChargeGlobe.Application/ChargeGlobe.Application.csproj src/ChargeGlobe.Application/
COPY src/ChargeGlobe.Infrastructure/ChargeGlobe.Infrastructure.csproj src/ChargeGlobe.Infrastructure/
COPY src/ChargeGlobe.Providers.Abstractions/ChargeGlobe.Providers.Abstractions.csproj src/ChargeGlobe.Providers.Abstractions/
COPY src/ChargeGlobe.Providers.EControl/ChargeGlobe.Providers.EControl.csproj src/ChargeGlobe.Providers.EControl/
COPY src/ChargeGlobe.Jobs/ChargeGlobe.Jobs.csproj src/ChargeGlobe.Jobs/
RUN dotnet restore src/ChargeGlobe.Jobs/ChargeGlobe.Jobs.csproj
COPY src/ChargeGlobe.Domain/ src/ChargeGlobe.Domain/
COPY src/ChargeGlobe.Application/ src/ChargeGlobe.Application/
COPY src/ChargeGlobe.Infrastructure/ src/ChargeGlobe.Infrastructure/
COPY src/ChargeGlobe.Providers.Abstractions/ src/ChargeGlobe.Providers.Abstractions/
COPY src/ChargeGlobe.Providers.EControl/ src/ChargeGlobe.Providers.EControl/
COPY src/ChargeGlobe.Jobs/ src/ChargeGlobe.Jobs/
RUN dotnet publish src/ChargeGlobe.Jobs/ChargeGlobe.Jobs.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["dotnet", "ChargeGlobe.Jobs.dll"]
