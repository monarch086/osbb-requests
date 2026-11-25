FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY angular.json tsconfig*.json ./
COPY src/ ./src/
RUN npm run build

FROM nginx:1.28-alpine
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist/osbb-admin/browser/ /usr/share/nginx/html/
