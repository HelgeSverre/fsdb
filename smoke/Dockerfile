# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS fsdb-build
WORKDIR /source
COPY global.json ./
COPY src/Fsdb/ src/Fsdb/
RUN dotnet publish src/Fsdb -c Release -o /output --no-self-contained -v minimal

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS fsdb
WORKDIR /app
COPY --from=fsdb-build /output/ ./
ENTRYPOINT ["dotnet", "Fsdb.dll", "--listen", "0.0.0.0", "--port", "3306"]

FROM golang:1.27-bookworm AS gitea
RUN apt-get update \
    && apt-get install --yes --no-install-recommends build-essential git git-lfs \
    && rm -rf /var/lib/apt/lists/*
RUN useradd --create-home --uid 10001 smoke \
    && mkdir -p /opt/gitea \
    && chown smoke:smoke /opt/gitea
ARG GITEA_REF
USER smoke
WORKDIR /opt/gitea
RUN git init \
    && git remote add origin https://github.com/go-gitea/gitea.git \
    && git fetch --depth=1 origin "${GITEA_REF}" \
    && git checkout --detach FETCH_HEAD \
    && git lfs install --local \
    && git lfs pull origin "${GITEA_REF}"
RUN go mod download
RUN make gitea
COPY smoke/targets/gitea.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php:8.5-cli-bookworm AS php-base
ENV COMPOSER_ALLOW_SUPERUSER=1
RUN apt-get update \
    && apt-get install --yes --no-install-recommends bash git unzip \
    && rm -rf /var/lib/apt/lists/*
COPY --from=composer:2.9.3 /usr/bin/composer /usr/local/bin/composer
COPY --from=mlocati/php-extension-installer:2 /usr/bin/install-php-extensions /usr/local/bin/
RUN install-php-extensions \
    apcu bcmath calendar curl dom gd intl mbstring mysqli opcache pcntl \
    pdo_mysql redis simplexml xml xmlreader xmlwriter zip

FROM php-base AS mediawiki
ARG MEDIAWIKI_REF
WORKDIR /opt/mediawiki
RUN git init \
    && git remote add origin https://github.com/wikimedia/mediawiki.git \
    && git fetch --depth=1 origin "${MEDIAWIKI_REF}" \
    && git checkout --detach FETCH_HEAD
RUN composer update --no-interaction --prefer-dist --no-progress
COPY smoke/targets/mediawiki.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php-base AS drupal
ARG DRUPAL_REF
WORKDIR /opt/drupal
RUN git init \
    && git remote add origin https://github.com/drupal/drupal.git \
    && git fetch --depth=1 origin "${DRUPAL_REF}" \
    && git checkout --detach FETCH_HEAD
RUN composer install --no-interaction --prefer-dist --no-progress
COPY smoke/targets/drupal.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php-base AS nextcloud
ARG NEXTCLOUD_REF
WORKDIR /opt/nextcloud
RUN git init \
    && git remote add origin https://github.com/nextcloud/server.git \
    && git fetch --depth=1 origin "${NEXTCLOUD_REF}" \
    && git checkout --detach FETCH_HEAD \
    && git submodule update --init --recursive --depth=1
RUN composer install --no-interaction --prefer-dist --no-progress
COPY smoke/targets/nextcloud.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php-base AS shopware
ENV COMPOSER_ROOT_VERSION=6.7.9999999-dev
ARG SHOPWARE_REF
WORKDIR /opt/shopware
RUN git init \
    && git remote add origin https://github.com/shopware/shopware.git \
    && git fetch --depth=1 origin "${SHOPWARE_REF}" \
    && git checkout --detach FETCH_HEAD
RUN composer install --no-interaction --prefer-dist --no-progress
COPY smoke/targets/shopware.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]
