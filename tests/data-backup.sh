#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
set -a
source "$project_root/.env.data.local"
set +a
compose=(docker compose -f "$project_root/compose.data.yml" exec -T postgres)
work_dir="$(mktemp -d)"
suffix="$(basename "$work_dir" | tr -cd '[:alnum:]' | tr '[:upper:]' '[:lower:]')"
source_db="osbb_backup_test_$suffix"
restored_db="osbb_restore_test_$suffix"
cleanup() {
  "${compose[@]}" dropdb -U osbb --if-exists "$source_db" >/dev/null 2>&1 || true
  "${compose[@]}" dropdb -U osbb --if-exists "$restored_db" >/dev/null 2>&1 || true
  rm -rf -- "$work_dir"
}
trap cleanup EXIT
"${compose[@]}" createdb -U osbb "$source_db"
"${compose[@]}" psql -U osbb -d "$source_db" -v ON_ERROR_STOP=1 -q -c "CREATE TABLE backup_test (id integer PRIMARY KEY, value text NOT NULL); INSERT INTO backup_test VALUES (1, 'перевірка');"
OSBB_POSTGRES_DB="$source_db" OSBB_BACKUP_DIR="$work_dir" "$project_root/scripts/osbb-data" backup >/dev/null
mapfile -t backups < <(find "$work_dir" -maxdepth 1 -type f -name '*.dump')
[[ ${#backups[@]} -eq 1 ]]
[[ "$(stat -c '%a' "${backups[0]}")" == '600' ]]
"${compose[@]}" createdb -U osbb "$restored_db"
"${compose[@]}" pg_restore -U osbb -d "$restored_db" --exit-on-error --no-owner --no-acl < "${backups[0]}"
actual="$("${compose[@]}" psql -U osbb -d "$restored_db" -Atqc 'SELECT value FROM backup_test WHERE id = 1')"
[[ "$actual" == 'перевірка' ]]
if OSBB_POSTGRES_DB="${source_db}_missing" OSBB_BACKUP_DIR="$work_dir" "$project_root/scripts/osbb-data" backup >/dev/null 2>&1; then
  printf '%s\n' 'Неочікуваний успіх для відсутньої бази.' >&2
  exit 1
fi
[[ "$(find "$work_dir" -maxdepth 1 -type f | wc -l)" -eq 1 ]]
printf '%s\n' 'Перевірку створення, відновлення та обробки помилки пройдено.'
