# Sourced by local data commands. The environment file is trusted shell input.
data_env_file="${OSBB_DATA_ENV_FILE:-$project_root/.env.data.dev}"
if [[ ! -f "$data_env_file" ]]; then
  printf 'Відсутній файл налаштувань %s.\n' "$data_env_file" >&2
  exit 1
fi
export OSBB_DATA_ENV_FILE="$(realpath -- "$data_env_file")"
set -a
source "$OSBB_DATA_ENV_FILE"
set +a
compose=(docker compose --env-file "$OSBB_DATA_ENV_FILE" -f "$project_root/compose.data.yml")
