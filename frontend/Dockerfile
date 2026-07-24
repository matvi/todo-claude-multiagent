# Build context is the frontend/ directory.
# Build:  docker build -f frontend/Dockerfile \
#           --build-arg VITE_API_BASE_URL=https://<todo-api FQDN> -t todo-web ./frontend

# build
FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
# VITE_API_BASE_URL is baked at build time; pass via --build-arg.
ARG VITE_API_BASE_URL
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL
RUN npm run build

# runtime
FROM nginx:1.27-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
