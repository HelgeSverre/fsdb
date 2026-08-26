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

FROM golang:1.27-bookworm AS gitea-build
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git-lfs make \
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
RUN rm -rf .git /home/smoke/.cache/go-build

FROM golang:1.27-bookworm AS gitea
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git git-lfs make \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 10001 smoke
COPY --from=gitea-build /go/pkg/mod/ /go/pkg/mod/
COPY --from=gitea-build --chown=smoke:smoke /opt/gitea/ /opt/gitea/
USER smoke
WORKDIR /opt/gitea
COPY smoke/targets/gitea.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php:8.5-cli-bookworm AS php-base
COPY --from=mlocati/php-extension-installer:2 /usr/bin/install-php-extensions /usr/local/bin/
RUN install-php-extensions \
    apcu bcmath calendar curl dom gd intl mbstring mysqli opcache pcntl \
    pdo_mysql redis simplexml xml xmlreader xmlwriter zip
COPY smoke/config/php-smoke.ini /usr/local/etc/php/conf.d/fsdb-smoke.ini

FROM php-base AS php-build
ENV COMPOSER_ALLOW_SUPERUSER=1
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git unzip \
    && rm -rf /var/lib/apt/lists/*
COPY --from=composer:2.9.3 /usr/bin/composer /usr/local/bin/composer

FROM php:8.4-cli-bookworm AS php84-base
COPY --from=mlocati/php-extension-installer:2 /usr/bin/install-php-extensions /usr/local/bin/
RUN install-php-extensions \
    apcu bcmath calendar curl dom gd intl mbstring mysqli opcache pcntl \
    pdo_mysql redis simplexml xml xmlreader xmlwriter zip
COPY smoke/config/php-smoke.ini /usr/local/etc/php/conf.d/fsdb-smoke.ini

FROM php-build AS mediawiki-build
ARG MEDIAWIKI_REF
WORKDIR /opt/mediawiki
RUN git init \
    && git remote add origin https://github.com/wikimedia/mediawiki.git \
    && git fetch --depth=1 origin "${MEDIAWIKI_REF}" \
    && git checkout --detach FETCH_HEAD
RUN composer update --no-interaction --prefer-dist --no-progress
RUN rm -rf .git

FROM php-base AS mediawiki
WORKDIR /opt/mediawiki
COPY --from=composer:2.9.3 /usr/bin/composer /usr/local/bin/composer
COPY --from=mediawiki-build /opt/mediawiki/ ./
COPY smoke/targets/mediawiki.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php-build AS drupal-build
ARG DRUPAL_REF
WORKDIR /opt/drupal
RUN git init \
    && git remote add origin https://github.com/drupal/drupal.git \
    && git fetch --depth=1 origin "${DRUPAL_REF}" \
    && git checkout --detach FETCH_HEAD
RUN composer install --no-interaction --prefer-dist --no-progress

FROM php-build AS drupal
WORKDIR /opt/drupal
RUN install-php-extensions pdo_sqlite \
    && apt-get update \
    && apt-get install --yes --no-install-recommends chromium chromium-driver \
    && rm -rf /var/lib/apt/lists/*
COPY --from=drupal-build /opt/drupal/ ./
COPY smoke/targets/drupal.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php-base AS nextcloud-base
RUN install-php-extensions bz2 ldap

FROM php-build AS nextcloud-build
ARG NEXTCLOUD_REF
WORKDIR /opt/nextcloud
RUN git init \
    && git remote add origin https://github.com/nextcloud/server.git \
    && git fetch --depth=1 origin "${NEXTCLOUD_REF}" \
    && git checkout --detach FETCH_HEAD \
    && git submodule update --init --recursive --depth=1
RUN composer install --no-interaction --prefer-dist --no-progress
RUN rm -rf .git 3rdparty/.git

FROM nextcloud-base AS nextcloud
ENV COMPOSER_ALLOW_SUPERUSER=1
WORKDIR /opt/nextcloud
RUN chown www-data:www-data .
COPY --from=composer:2.9.3 /usr/bin/composer /usr/local/bin/composer
COPY --from=nextcloud-build --chown=www-data:www-data /opt/nextcloud/ ./
COPY smoke/targets/nextcloud.sh /usr/local/bin/fsdb-smoke
USER www-data
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php-build AS shopware-build
ENV COMPOSER_ROOT_VERSION=6.7.9999999-dev
ARG SHOPWARE_REF
WORKDIR /opt/shopware
RUN git init \
    && git remote add origin https://github.com/shopware/shopware.git \
    && git fetch --depth=1 origin "${SHOPWARE_REF}" \
    && git checkout --detach FETCH_HEAD
RUN composer install --no-interaction --prefer-dist --no-progress
RUN rm -rf .git

FROM php-base AS shopware
WORKDIR /opt/shopware
COPY --from=shopware-build /opt/shopware/ ./
COPY smoke/targets/shopware.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM node:22.23.1-bookworm-slim AS ghost-build
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 \
    PUPPETEER_SKIP_DOWNLOAD=true
RUN apt-get update \
    && apt-get install --yes --no-install-recommends build-essential ca-certificates git python3 \
    && rm -rf /var/lib/apt/lists/* \
    && corepack enable
ARG GHOST_REF
WORKDIR /opt/ghost
RUN git init \
    && git remote add origin https://github.com/TryGhost/Ghost.git \
    && git fetch --depth=1 origin "${GHOST_REF}" \
    && git checkout --detach FETCH_HEAD \
    && git submodule update --init --recursive --depth=1
RUN pnpm install --frozen-lockfile
RUN pnpm --filter ghost... build \
    && pnpm --dir ghost/core build:assets
RUN rm -rf \
    .git \
    ghost/core/content/themes/casper/.git \
    ghost/core/content/themes/source/.git

FROM node:22.23.1-bookworm-slim AS ghost
WORKDIR /opt/ghost
COPY --from=ghost-build /opt/ghost/ ./
COPY smoke/targets/ghost.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php84-base AS moodle-base
RUN apt-get update \
    && apt-get install --yes --no-install-recommends locales \
    && echo 'en_AU.UTF-8 UTF-8' > /etc/locale.gen \
    && locale-gen \
    && rm -rf /var/lib/apt/lists/*
ENV LANG=en_AU.UTF-8 \
    LC_ALL=en_AU.UTF-8

FROM moodle-base AS moodle-build
ENV COMPOSER_ALLOW_SUPERUSER=1
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git unzip \
    && rm -rf /var/lib/apt/lists/*
COPY --from=composer:2.9.3 /usr/bin/composer /usr/local/bin/composer
ARG MOODLE_REF
WORKDIR /opt/moodle
RUN git init \
    && git remote add origin https://github.com/moodle/moodle.git \
    && git fetch --depth=1 origin "${MOODLE_REF}" \
    && git checkout --detach FETCH_HEAD
RUN composer install --no-interaction --prefer-dist --no-progress
RUN rm -rf .git

FROM moodle-base AS moodle
WORKDIR /opt/moodle
COPY --from=moodle-build /opt/moodle/ ./
COPY smoke/config/moodle.php /opt/moodle/public/config.php
COPY smoke/targets/moodle.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php-build AS wordpress-build
ARG WORDPRESS_REF
WORKDIR /opt/wordpress
RUN git init \
    && git remote add origin https://github.com/WordPress/wordpress-develop.git \
    && git fetch --depth=1 origin "${WORDPRESS_REF}" \
    && git checkout --detach FETCH_HEAD
RUN composer update --with-all-dependencies --no-interaction --prefer-dist --no-progress
RUN rm -rf .git

FROM php-base AS wordpress
WORKDIR /opt/wordpress
COPY --from=wordpress-build /opt/wordpress/ ./
COPY smoke/config/wordpress.php /opt/wordpress/wp-tests-config.php
COPY smoke/targets/wordpress.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM ruby:4.0.6-bookworm AS rails-build
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git libmariadb-dev \
    && rm -rf /var/lib/apt/lists/* \
    && gem install bundler --version 4.0.12 --no-document
ARG RAILS_REF
WORKDIR /opt/rails
RUN git init \
    && git remote add origin https://github.com/rails/rails.git \
    && git fetch --depth=1 origin "${RAILS_REF}" \
    && git checkout --detach FETCH_HEAD
RUN bundle install --jobs=4 --retry=3
RUN rm -rf .git /usr/local/bundle/cache

FROM ruby:4.0.6-slim-bookworm AS rails
RUN apt-get update \
    && apt-get install --yes --no-install-recommends libmariadb3 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /opt/rails
COPY --from=rails-build /usr/local/bundle/ /usr/local/bundle/
COPY --from=rails-build /opt/rails/ ./
COPY smoke/config/rails.yml /usr/local/share/fsdb-rails.yml
COPY smoke/targets/rails.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]

FROM php-base AS magento-base
RUN install-php-extensions ftp soap sockets xsl

FROM magento-base AS magento-build
ENV COMPOSER_MEMORY_LIMIT=-1
ENV COMPOSER_ALLOW_SUPERUSER=1
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git unzip \
    && rm -rf /var/lib/apt/lists/*
COPY --from=composer:2.9.3 /usr/bin/composer /usr/local/bin/composer
ARG MAGENTO_REF
WORKDIR /opt/magento
RUN git init \
    && git remote add origin https://github.com/magento/magento2.git \
    && git fetch --depth=1 origin "${MAGENTO_REF}" \
    && git checkout --detach FETCH_HEAD
RUN composer install --no-interaction --prefer-dist --no-progress
RUN rm -rf .git

FROM magento-base AS magento
ENV COMPOSER_MEMORY_LIMIT=-1
WORKDIR /opt/magento
COPY --from=magento-build /opt/magento/ ./
COPY smoke/targets/magento.sh /usr/local/bin/fsdb-smoke
ENTRYPOINT ["/usr/local/bin/fsdb-smoke"]
