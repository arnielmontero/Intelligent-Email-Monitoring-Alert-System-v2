FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Iemas.sln .
COPY src/Iemas.Api/Iemas.Api.csproj src/Iemas.Api/
COPY src/Iemas.Domain/Iemas.Domain.csproj src/Iemas.Domain/
COPY src/Iemas.Application/Iemas.Application.csproj src/Iemas.Application/
COPY src/Iemas.Infrastructure/Iemas.Infrastructure.csproj src/Iemas.Infrastructure/
COPY tests/Iemas.Tests/Iemas.Tests.csproj tests/Iemas.Tests/
RUN dotnet restore src/Iemas.Api/Iemas.Api.csproj

COPY . .
RUN dotnet publish src/Iemas.Api/Iemas.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Iemas.Api.dll"]
