# Build context is the frontend/ directory.
# Build:  docker build -f frontend/Dockerfile \
#           --build-arg VITE_API_BASE_URL=https://<todo-api FQDN> \
#           --build-arg VITE_ENTRA_TENANT_SUBDOMAIN=<tenant-subdomain> \
#           --build-arg VITE_ENTRA_TENANT_ID=<external-tenant-guid> \
#           --build-arg VITE_ENTRA_SPA_CLIENT_ID=<spa-client-id> \
#           --build-arg VITE_API_SCOPE=api://<api-client-id>/Todos.ReadWrite \
#           -t todo-web ./frontend
#
# Every build arg is a PUBLIC identifier (specs §16.6(3)) — a URL, a tenant subdomain, a
# tenant GUID, a public-client id, a scope name. No secret is ever passed to this build.
# Because they are baked into the bundle, the image is environment-specific and must be
# rebuilt whenever any of them changes (§16.9.1).

# build
FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
# Vite variables are baked at build time; pass via --build-arg.
ARG VITE_API_BASE_URL
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
ARG VITE_ENTRA_TENANT_SUBDOMAIN
ENV VITE_ENTRA_TENANT_SUBDOMAIN=$VITE_ENTRA_TENANT_SUBDOMAIN
ARG VITE_ENTRA_TENANT_ID
ENV VITE_ENTRA_TENANT_ID=$VITE_ENTRA_TENANT_ID
ARG VITE_ENTRA_SPA_CLIENT_ID
ENV VITE_ENTRA_SPA_CLIENT_ID=$VITE_ENTRA_SPA_CLIENT_ID
ARG VITE_API_SCOPE
ENV VITE_API_SCOPE=$VITE_API_SCOPE
RUN npm run build

# runtime
FROM nginx:1.27-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
