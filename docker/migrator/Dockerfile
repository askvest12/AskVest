FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore AskVest.slnx --locked-mode
RUN dotnet publish src/AskVest.Migrator/AskVest.Migrator.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG INCLUDE_SAMPLE_DATA=false
RUN groupadd --system askvest && useradd --system --gid askvest --no-create-home askvest
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /src/data ./data
RUN if [ "$INCLUDE_SAMPLE_DATA" != "true" ]; then rm -rf /app/data/catalog-sample; fi
USER askvest
ENTRYPOINT ["dotnet", "AskVest.Migrator.dll"]
