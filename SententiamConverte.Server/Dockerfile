# Teamserver als Container (Linux, NAS). Bauen im Hauptordner des Repos:
#   docker build -f SententiamConverte.Server/Dockerfile -t sententiam-server .
#   docker run -d -p 5280:5280 -v sententiam-daten:/daten --name sententiam sententiam-server
# Beim ersten Start steht der Admin-Schlüssel im Log:  docker logs sententiam

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish SententiamConverte.Server/SententiamConverte.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV SENTENTIAM_SERVER_DATA=/daten
VOLUME /daten
EXPOSE 5280
ENTRYPOINT ["dotnet", "SententiamConverte.Server.dll"]
