# syntax=docker/dockerfile:1
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci
COPY . .
# Vite inlines import.meta.env.VITE_* at build time (tree-shaken into the bundle), so the
# Entra SPA client id + API scope must be present as build-time ARG/ENV here, not just at
# container runtime. Empty defaults => a build with no --build-arg produces today's open-mode
# image (see src/auth/msal.ts, which no-ops when these are unset).
ARG VITE_ENTRA_CLIENT_ID=""
ARG VITE_ENTRA_TENANT_ID=""
ARG VITE_API_SCOPE=""
ENV VITE_ENTRA_CLIENT_ID=$VITE_ENTRA_CLIENT_ID \
    VITE_ENTRA_TENANT_ID=$VITE_ENTRA_TENANT_ID \
    VITE_API_SCOPE=$VITE_API_SCOPE

# The stakeholder-demo image, and ONLY that image, ships the fixture proj-demo.
#   docker build --build-arg ENABLE_DEMO=true ...
#   az acr build --build-arg ENABLE_DEMO=true ...
# Default false: a normal frontend image carries no mock runtime and no worker script.
# Never point real production at a demo image, and give the demo its own hostname — MSW
# registers a service worker at the origin scope (see the note in src/mocks/demo.ts).
ARG ENABLE_DEMO=false
ENV VITE_ENABLE_DEMO=$ENABLE_DEMO

# mockServiceWorker.js is git-ignored, so a clean build context has none. Generate it
# here for the demo image (msw is a devDep, present after `npm ci`) so the artifact is
# reproducible and does not depend on a file that happened to exist on someone's disk.
# When ENABLE_DEMO is false, vite's publicDir is off and nothing is emitted regardless.
RUN if [ "$ENABLE_DEMO" = "true" ]; then npx msw init public --save >/dev/null; fi

# `npm run build` runs tsc --noEmit before vite build, so a type error fails the image.
RUN npm run build

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
# Matches the ACA frontend app's targetPort in infra/modules/compute.bicep.
EXPOSE 80
