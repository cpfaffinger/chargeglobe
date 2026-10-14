# Build context: src/web
FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npx ng build --configuration production

FROM nginx:1.29-alpine
RUN apk add --no-cache openssl
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --chmod=755 10-self-signed-cert.sh /docker-entrypoint.d/10-self-signed-cert.sh
COPY --from=build /app/dist/web/browser /usr/share/nginx/html
EXPOSE 443
