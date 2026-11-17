#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT
mkdir -p "$work_dir/bin" "$work_dir/runtime" "$work_dir/backups"
cat > "$work_dir/bin/docker" <<'DOCKER'
#!/usr/bin/env bash
printf 'docker %s\n' "$*" >> "$WEEKLY_TEST_LOG"
case " $* " in
  *' pg_dump '*) printf 'synthetic archive' ;;
  *' pg_restore '*) cat >/dev/null ;;
esac
DOCKER
cat > "$work_dir/bin/dotnet" <<'DOTNET'
#!/usr/bin/env bash
printf 'dotnet %s\n' "$*" >> "$WEEKLY_TEST_LOG"
if [[ "${WEEKLY_TEST_FAIL:-0}" == 1 ]]; then exit 1; fi
DOTNET
chmod +x "$work_dir/bin/docker" "$work_dir/bin/dotnet"
export PATH="$work_dir/bin:$PATH"
export XDG_RUNTIME_DIR="$work_dir/runtime"
export OSBB_BACKUP_DIR="$work_dir/backups"
export WEEKLY_TEST_LOG="$work_dir/calls"
"$project_root/scripts/osbb-data-weekly" >/dev/null
[[ "$(find "$work_dir/backups" -maxdepth 1 -name '*.dump' | wc -l)" -eq 1 ]]
python3 - "$WEEKLY_TEST_LOG" <<'PY'
import sys
text = open(sys.argv[1], encoding='utf-8').read()
assert text.index(' up ') < text.index('dotnet ') < text.index(' pg_dump ') < text.index(' pg_restore ')
assert '--stage dev --region eu-central-1' in text
PY
: > "$WEEKLY_TEST_LOG"
if WEEKLY_TEST_FAIL=1 "$project_root/scripts/osbb-data-weekly" >/dev/null 2>&1; then
  printf '%s\n' 'Помилка імпорту не зупинила завдання.' >&2
  exit 1
fi
if grep -q 'pg_dump' "$WEEKLY_TEST_LOG"; then
  printf '%s\n' 'Бекап запущено після помилки імпорту.' >&2
  exit 1
fi
exec 8>"$work_dir/runtime/osbb-data-weekly.lock"
flock -n 8
if "$project_root/scripts/osbb-data-weekly" >/dev/null 2>&1; then
  printf '%s\n' 'Одночасний запуск не заблоковано.' >&2
  exit 1
fi
printf '%s\n' 'Перевірку порядку, помилок і блокування пройдено.'
