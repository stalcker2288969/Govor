# Деплой на VPS Timeweb через GitHub Actions

После включения: push в `master` → существующие тесты SQLite/PostgreSQL → сборка
Dockerfile → публикация в GitHub Container Registry (GHCR) → SSH → миграции →
обновление контейнеров → проверка `/server/ready`. PR выполняет только тесты.
Доступен и ручной запуск workflow `.NET` для `master`.

Базовая конфигурация рассчитана на один VPS Linux x86_64 и внешнюю PostgreSQL.
Обновление допускает короткий перерыв. База не переносится автоматически.

## Постоянные данные

```text
/opt/govor/
  shared/runtime.env       # секреты API, создаются вручную
  shared/deploy.env        # домен
  data/logs/               # /app/logs в контейнере
  data/uploads/            # /app/uploads в контейнере
  releases/<commit-run>/   # Compose, Caddyfile, deploy.sh, image.env
  current -> releases/... # последний успешный релиз
```

`logs` и `uploads` подключены через bind mounts с фиксированными абсолютными путями.
GitHub Actions копирует только конфигурацию нового релиза и заменяет контейнеры.
Данные находятся вне репозитория и каталога релиза. Скрипт не выполняет очистку,
`git clean`, `rsync --delete`, `docker compose down -v` или удаление Docker volumes.
Если папок данных нет, деплой завершится ошибкой вместо создания пустого хранилища.
Приложение по-прежнему может удалять файлы по пользовательским запросам.

Сертификаты Caddy хранятся в именованных Docker volumes проекта `govor`.
Ротация Docker stdout ограничена; файловые логи API сохраняются и требуют контроля
свободного места. Настройте отдельные резервные копии БД и `/opt/govor/data` вне VPS:
постоянный диск сам по себе не является резервной копией.

## 1. Подготовить сервер один раз

Установите Docker Engine и Compose plugin по официальной инструкции Docker для
выбранной ОС. Нужны Bash, `flock` (util-linux) и OpenSSH. Compose должен поддерживать
несколько `--env-file` и `up --wait` (например, актуальный Compose v2).

Создайте пользователя `deploy` с SSH-доступом по отдельному ключу и доступом к
Docker. Членство в группе `docker` фактически даёт административные права на VPS.
На сервере, от администратора:

```bash
sudo usermod -aG docker deploy
sudo install -d -o deploy -g deploy -m 750 /opt/govor
sudo install -d -o deploy -g deploy -m 700 /opt/govor/shared
sudo install -d -o deploy -g deploy -m 750 /opt/govor/releases /opt/govor/data
sudo install -d -o deploy -g deploy -m 750 /opt/govor/data/logs /opt/govor/data/uploads
```

Переподключитесь по SSH после изменения групп. Добавьте публичный ключ Actions в
`~deploy/.ssh/authorized_keys`. Приватный ключ останется в GitHub Secrets.

Создайте `/opt/govor/shared/runtime.env` по корневому `.env.example` с реальными
значениями текущего приложения. Сохраните существующие JWT/refresh-секреты при
переносе, если не требуется отзывать сессии. Формат — Docker env file; значения
со знаком `$` заключайте в одинарные кавычки, чтобы Compose не интерполировал их.

Создайте `/opt/govor/shared/deploy.env` по `deploy/deploy.env.example`:

```dotenv
GOVOR_DOMAIN=api.example.com
```

```bash
chmod 600 /opt/govor/shared/runtime.env /opt/govor/shared/deploy.env
docker compose version
docker info >/dev/null
```

Настройте DNS домена на IP VPS, входящие TCP 80/443 на Caddy и SSH для деплоя.
API 8080 и PostgreSQL не публикуются этим Compose в интернет. Разрешите новому VPS
подключение к внешней БД. Сертификат PostgreSQL из `Govor.API/certs` уже включён
в Dockerfile через publish проекта.

Для приватного GHCR-образа войдите в реестр **под пользователем deploy** один раз:

```bash
read -rsp 'GHCR token (read:packages): ' ghcr_token; echo
printf '%s' "$ghcr_token" | docker login ghcr.io -u YOUR_GITHUB_LOGIN --password-stdin
unset ghcr_token
```

Используйте токен с доступом к пакету и `read:packages` (при необходимости разрешите
SSO организации). Токен сервера нужен для скачивания приватного образа; Actions
публикует образ своим временным `GITHUB_TOKEN`. Публичный пакет не требует входа
на VPS. Не помещайте токен в runtime.env или Git.

## 2. Перенести существующие файлы

До переключения клиентов сохраните копию БД и существующих `uploads`/`logs`.
Остановите запись в старый API на время финального копирования, чтобы файлы и БД
оставались согласованными. Скопируйте **содержимое** прежней папки uploads в
`/opt/govor/data/uploads`, сохраняя все вложенные пути, и аналогично logs в
`/opt/govor/data/logs`. Не создавайте лишний уровень `uploads/uploads`.
Если БД остаётся прежней, её повторный импорт не нужен.

Сначала проверьте новую установку, затем переключите домен и отключите старый API.
Если адрес API меняется, обновите настройки мобильного клиента. Для проверки до
смены DNS можно использовать `curl --resolve` с новым IP после настройки TLS.

## 3. Настроить GitHub

В Settings → Environments создайте `production`. Ограничьте deployment branches
веткой `master`. Для автоматического деплоя без ручного подтверждения не включайте
required reviewers. Добавьте environment secrets:

| Secret | Значение |
| --- | --- |
| `VPS_HOST` | IP или домен SSH сервера |
| `VPS_USER` | `deploy` |
| `VPS_SSH_KEY` | Полный приватный SSH-ключ без passphrase, включая BEGIN/END |
| `VPS_KNOWN_HOSTS` | Проверенная запись host key сервера в формате known_hosts |

Получить запись можно через `ssh-keyscan -p 22 SERVER`, но перед сохранением
обязательно сверить fingerprint с ключом через консоль Timeweb:
`ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`.
Для нестандартного порта known_hosts использует `[host]:port`.

В Repository → Settings → Secrets and variables → Actions → Variables:

| Variable | Значение |
| --- | --- |
| `VPS_DEPLOY_ENABLED` | `true`, только после подготовки сервера и переноса данных |
| `VPS_SSH_PORT` | Необязательно; по умолчанию `22` |

Workflow публикует `ghcr.io/<owner>/<repo>:<commit>`; запускается точный digest
`@sha256:...`. Actions получает `packages: write` только в задаче публикации.
Если пакет уже существует, предоставьте репозиторию Actions-доступ к нему в
настройках пакета. Пока `VPS_DEPLOY_ENABLED` не включён, работают только тесты.

## 4. Обновления и диагностика

Сделайте push в `master` или запустите `.NET` → Run workflow → `master`.
Не удаляйте резервную копию БД перед потенциально разрушающими миграциями.
Скрипт сначала скачивает образ, затем запускает `--migrate-only`. При ошибке
миграции контейнер текущего API не заменяется; сама миграция уже могла изменить БД.
При ошибке запуска job падает; автоматического отката схемы или контейнера нет.
Ссылка `current` обновляется только после успешной проверки готовности.
Деплои сериализуются GitHub concurrency и серверной блокировкой `flock`.

Посмотреть контейнеры и логи на VPS:

```bash
docker compose --project-name govor \
  --env-file /opt/govor/shared/deploy.env \
  --env-file /opt/govor/current/image.env \
  -f /opt/govor/current/compose.yaml ps
docker compose --project-name govor \
  --env-file /opt/govor/shared/deploy.env \
  --env-file /opt/govor/current/image.env \
  -f /opt/govor/current/compose.yaml logs --tail=100 api proxy
```

При первом неудачном деплое вместо `current` используйте путь релиза из Actions.
Проверьте HTTPS `/server/ready`, вход, старые вложения, отправку нового файла и
SignalR. Чтобы проверить сохранность, запишите контрольные суммы нескольких
существующих файлов в uploads, выполните повторный деплой и сравните их.

Для возврата к прежнему образу выберите совместимый со схемой БД успешный релиз
из `releases`, возьмите digest из его `image.env` и выполните
`bash /opt/govor/releases/<release>/deploy.sh ghcr.io/<owner>/<repo>@sha256:<digest>`.
Старый код не отменяет миграции; несовместимая схема требует отдельного плана
восстановления БД. Старые образы и каталоги релизов автоматически не очищаются.

Официальные инструкции:
- https://docs.docker.com/engine/install/ubuntu/
- https://docs.docker.com/engine/storage/bind-mounts/
- https://docs.github.com/en/actions/tutorials/publish-packages/publish-docker-images
- https://caddyserver.com/docs/quick-starts/reverse-proxy
