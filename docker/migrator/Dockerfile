FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore AskVest.slnx --locked-mode
RUN dotnet publish src/AskVest.Migrator/AskVest.Migrator.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN groupadd --system askvest && useradd --system --gid askvest --no-create-home askvest
WORKDIR /app
COPY --from=build /app/publish .
USER askvest
ENTRYPOINT ["dotnet", "AskVest.Migrator.dll"]
