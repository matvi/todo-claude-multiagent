# Build context MUST be the backend/ directory so the src/ layout resolves.
# From repo root:  docker build -f backend/src/TodoApi/Dockerfile -t todo-api ./backend

# build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/TodoApi/TodoApi.csproj src/TodoApi/
RUN dotnet restore src/TodoApi/TodoApi.csproj
COPY . .
RUN dotnet publish src/TodoApi/TodoApi.csproj -c Release -o /app

# runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TodoApi.dll"]
