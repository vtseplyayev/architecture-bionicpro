FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ReportsApi.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000

# Сервис работает с ПДн — не от root.
USER $APP_UID

ENTRYPOINT ["dotnet", "ReportsApi.dll"]
