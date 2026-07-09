# syntax=docker/dockerfile:1
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci
COPY . .
# `npm run build` runs tsc --noEmit before vite build, so a type error fails the image.
RUN npm run build

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
# Matches the ACA frontend app's targetPort in infra/modules/compute.bicep.
EXPOSE 80
