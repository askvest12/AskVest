FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore AskVest.slnx --locked-mode
RUN dotnet publish src/AskVest.Api/AskVest.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG INCLUDE_SAMPLE_CONTENT=false
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
RUN groupadd --system askvest && useradd --system --gid askvest --no-create-home askvest
WORKDIR /app
COPY --from=build /app/publish .
COPY data/learning ./data/learning
COPY data/learning-sample ./data/learning-sample
RUN if [ "$INCLUDE_SAMPLE_CONTENT" != "true" ]; then rm -rf /app/data/learning-sample; fi
USER askvest
EXPOSE 8080
ENTRYPOINT ["dotnet", "AskVest.Api.dll"]
