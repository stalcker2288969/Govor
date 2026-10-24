#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

release_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
image="${1:?Usage: bash deploy.sh ghcr.io/owner/repo@sha256:digest}"
[[ "$image" =~ ^ghcr\.io/[a-z0-9._/-]+@sha256:[a-f0-9]{64}$ ]] || {
    echo "Expected an immutable GHCR image digest" >&2
    exit 1
}

# The lock also protects against simultaneous manual and Actions deployments.
exec 9>/opt/govor/deploy.lock
flock -w 600 9
test -s /opt/govor/shared/runtime.env
test -s /opt/govor/shared/deploy.env
test -d /opt/govor/data/logs
test -d /opt/govor/data/uploads
printf 'GOVOR_IMAGE=%s\n' "$image" > "$release_dir/image.env"

compose=(docker compose --project-name govor
    --env-file /opt/govor/shared/deploy.env
    --env-file "$release_dir/image.env"
    -f "$release_dir/compose.yaml")

# Validate silently: rendered config contains runtime secrets.
"${compose[@]}" config --quiet
"${compose[@]}" pull
# A migration failure leaves the existing API container running.
"${compose[@]}" run --rm --no-deps api --migrate-only
"${compose[@]}" up -d --wait --wait-timeout 180
# Check readiness again, independently of container startup order.
"${compose[@]}" exec -T api curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/server/ready

# Keep successful release configuration for a manual rollback.
ln -sfn "$release_dir" /opt/govor/current
echo "Deployment ready: $image"
