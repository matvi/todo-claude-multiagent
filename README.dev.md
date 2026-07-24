# Todo App — Developer Quickstart

A minimal Todo demo: React (Vite) SPA + .NET 10 Web API + PostgreSQL.
No auth, single anonymous user. See `.pipeline/specs.md` for the full spec.

## Repository layout

```
backend/            .NET 10 Web API (TodoApi.sln)
  src/TodoApi/      API project (controllers, EF Core, migrations, Dockerfile)
  tests/            xUnit test project (populated by the tester agent)
frontend/           Vite + React + TypeScript SPA
docker-compose.yml  Local Postgres only
scripts/
  deploy-azure.sh   Azure runbook (documentation — a human runs it)
.env.example        Local env var template
```

## Prerequisites

- .NET 10 SDK (`dotnet --version` → 10.0.x)
- `dotnet-ef` 10 global tool (`dotnet tool install --global dotnet-ef --version 10.0.0`)
- Node.js 22 + npm
- Docker (for local Postgres)

## Run locally

1. **Start Postgres**
   ```bash
   docker compose up -d
   ```
   Postgres listens on `localhost:5432` (db `tododb`, user/pass `todo`/`todo`).

2. **Backend** — from `backend/src/TodoApi`:
   ```bash
   dotnet run
   ```
   - Migrations are applied automatically at startup. To apply manually instead:
     `dotnet ef database update`.
   - API: <http://localhost:8080>  •  Swagger: <http://localhost:8080/swagger>
   - Health: <http://localhost:8080/health> → `{"status":"ok"}`
   - Dev connection string + CORS (`http://localhost:5173`) come from
     `appsettings.Development.json`.

3. **Frontend** — from `frontend`:
   ```bash
   npm install
   npm run dev
   ```
   SPA: <http://localhost:5173> (reads `VITE_API_BASE_URL` from `.env.development`).

## API surface

Base path `/api/todos`, JSON camelCase.

| Method | Path              | Success | Notes                          |
|--------|-------------------|---------|--------------------------------|
| GET    | `/api/todos`      | 200     | `TodoResponse[]`, newest first |
| GET    | `/api/todos/{id}` | 200     | 404 if not found               |
| POST   | `/api/todos`      | 201     | `Location` header; 400 on validation |
| PUT    | `/api/todos/{id}` | 200     | 404 / 400                      |
| DELETE | `/api/todos/{id}` | 204     | 404 if not found               |
| GET    | `/health`         | 200     | probe                          |

## Build the container images

```bash
# Backend (build context = backend/)
docker build -f backend/src/TodoApi/Dockerfile -t todo-api ./backend

# Frontend (bake the API URL at build time)
docker build -f frontend/Dockerfile \
  --build-arg VITE_API_BASE_URL=http://localhost:8080 -t todo-web ./frontend
```

Both containers listen on port 8080.

## Deploy to Azure

Deployment is manual and documented as a runbook — see `scripts/deploy-azure.sh`
(also summarized in `.pipeline/changes.md`). It provisions a resource group,
ACR, a Container Apps environment, a Postgres Flexible Server, and the two
Container Apps, in the order the frontend's build-time API URL requires.

```bash
export PG_ADMIN_PASSWORD='<a-strong-password>'   # never commit
./scripts/deploy-azure.sh
```
