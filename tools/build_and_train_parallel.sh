#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

DEFAULT_CONFIGS=(
  "ml-agents/config/ppo/AntBehavior_Base.yaml"
  "ml-agents/config/ppo/AntBehavior_Team.yaml"
  "ml-agents/config/ppo/AntBehavior_Reciprocal.yaml"
  "ml-agents/config/ppo/AntBehavior_TeamReciprocal.yaml"
)

BUILD_NAME=""
BUILD_TARGET="StandaloneOSX"
BUILD_OUTPUT_DIR="$PROJECT_ROOT/Builds"
RESULTS_DIR="$PROJECT_ROOT/results"
LOG_ROOT="$PROJECT_ROOT/logs/training"
BASE_PORT=5005
PORT_STRIDE=10
STRICT_PORTS=0
NO_BUILD=0
NO_GRAPHICS=1
FORCE=0
RESUME=0
UNITY_BIN="${UNITY_PATH:-}"
MLAGENTS_LEARN_OVERRIDE="${MLAGENTS_LEARN:-}"
CONFIGS=()
EXTRA_MLAGENTS_ARGS=()
LEARN_CMD=()
CHILD_PIDS=()

usage() {
  cat <<'USAGE'
Usage:
  tools/build_and_train_parallel.sh BUILD_NAME [options]

Builds the Unity project to Builds/<name>.app, then starts one mlagents-learn
process per training config.

Defaults:
  configs: AntBehavior_Base, Team, Reciprocal, TeamReciprocal
  ports:   5005, 5015, 5025, 5035, or the next free block if busy
  logs:    logs/training/<build-name>/*.log

Options:
  --build-name NAME       Build/output name. Same as positional BUILD_NAME.
  --config PATH           Add a config file. Repeat to override the default four.
  --configs A,B,C,D       Comma-separated config list. Overrides the default four.
  --base-port PORT        Preferred first trainer port. Default: 5005.
  --port-stride N         Port spacing between runs. Default: 10.
  --strict-ports          Fail if requested ports are busy instead of shifting.
  --build-target TARGET   Unity build target. Default: StandaloneOSX.
  --build-output-dir DIR  Where builds are written. Default: ./Builds.
  --results-dir DIR       ML-Agents results dir. Default: ./results.
  --logs-dir DIR          Per-run logs dir root. Default: ./logs/training.
  --unity PATH            Unity executable. Defaults to UNITY_PATH or Hub version.
  --mlagents-learn PATH   mlagents-learn executable. Defaults to MLAGENTS_LEARN or PATH.
  --no-build              Reuse Builds/<name>.app instead of building.
  --graphics              Do not pass --no-graphics to mlagents-learn.
  --force                 Pass --force to mlagents-learn.
  --resume                Pass --resume to mlagents-learn.
  --help                  Show this help.

Everything after -- is forwarded to every mlagents-learn process.

Examples:
  tools/build_and_train_parallel.sh testRefactor --force
  tools/build_and_train_parallel.sh testRefactor --no-build --base-port 5105
  tools/build_and_train_parallel.sh testRefactor -- --time-scale 20
USAGE
}

log() {
  printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*"
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

abs_path() {
  local path="$1"
  if [[ "$path" == /* ]]; then
    printf '%s\n' "$path"
  else
    printf '%s/%s\n' "$PROJECT_ROOT" "$path"
  fi
}

slugify() {
  printf '%s' "$1" \
    | tr '[:upper:]' '[:lower:]' \
    | sed -E 's/[^a-z0-9._-]+/-/g; s/^-+//; s/-+$//'
}

normalize_build_target() {
  case "$1" in
    mac|macos|osx|StandaloneOSX|standaloneosx)
      printf 'StandaloneOSX\n'
      ;;
    linux|linux64|StandaloneLinux64|standalonelinux64)
      printf 'StandaloneLinux64\n'
      ;;
    win|windows|windows64|StandaloneWindows64|standalonewindows64)
      printf 'StandaloneWindows64\n'
      ;;
    *)
      printf '%s\n' "$1"
      ;;
  esac
}

player_path() {
  local output_dir="$1"
  local build_name="$2"
  local build_target="$3"

  case "$build_target" in
    StandaloneOSX)
      printf '%s/%s.app\n' "$output_dir" "$build_name"
      ;;
    StandaloneWindows|StandaloneWindows64)
      printf '%s/%s.exe\n' "$output_dir" "$build_name"
      ;;
    StandaloneLinux64)
      printf '%s/%s\n' "$output_dir" "$build_name"
      ;;
    *)
      printf '%s/%s\n' "$output_dir" "$build_name"
      ;;
  esac
}

port_in_use() {
  local port="$1"
  command -v lsof >/dev/null 2>&1 || return 1
  lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1
}

describe_ports() {
  local base_port="$1"
  local count="$2"
  local ports=()
  local index

  for ((index = 0; index < count; index++)); do
    ports+=("$((base_port + index * PORT_STRIDE))")
  done

  printf '%s\n' "${ports[*]}"
}

port_block_is_free() {
  local base_port="$1"
  local count="$2"
  local index port

  for ((index = 0; index < count; index++)); do
    port=$((base_port + index * PORT_STRIDE))
    if port_in_use "$port"; then
      return 1
    fi
  done

  return 0
}

select_base_port() {
  local count="$1"
  local candidate="$BASE_PORT"
  local max_port=65535
  local last_offset=$(((count - 1) * PORT_STRIDE))

  if ! command -v lsof >/dev/null 2>&1; then
    log "lsof not found; skipping port preflight for $(describe_ports "$BASE_PORT" "$count")" >&2
    printf '%s\n' "$BASE_PORT"
    return
  fi

  if port_block_is_free "$candidate" "$count"; then
    printf '%s\n' "$candidate"
    return
  fi

  if ((STRICT_PORTS)); then
    die "Requested ports are busy: $(describe_ports "$BASE_PORT" "$count"). Use --base-port with a free block or omit --strict-ports."
  fi

  while ((candidate + last_offset <= max_port)); do
    if port_block_is_free "$candidate" "$count"; then
      log "Requested ports $(describe_ports "$BASE_PORT" "$count") are not all free; using $(describe_ports "$candidate" "$count") instead." >&2
      printf '%s\n' "$candidate"
      return
    fi

    candidate=$((candidate + PORT_STRIDE))
  done

  die "Could not find a free block of $count ports starting from $BASE_PORT."
}

detect_unity() {
  if [[ -n "$UNITY_BIN" ]]; then
    [[ -x "$UNITY_BIN" ]] || die "Unity executable is not runnable: $UNITY_BIN"
    return
  fi

  local editor_version=""
  if [[ -f "$PROJECT_ROOT/ProjectSettings/ProjectVersion.txt" ]]; then
    editor_version="$(awk '/m_EditorVersion:/{print $2; exit}' "$PROJECT_ROOT/ProjectSettings/ProjectVersion.txt")"
  fi

  local candidates=()
  if [[ -n "$editor_version" ]]; then
    candidates+=("/Applications/Unity/Hub/Editor/$editor_version/Unity.app/Contents/MacOS/Unity")
  fi
  candidates+=("/Applications/Unity/Unity.app/Contents/MacOS/Unity")

  if command -v unity >/dev/null 2>&1; then
    candidates+=("$(command -v unity)")
  fi

  local candidate
  for candidate in "${candidates[@]}"; do
    if [[ -x "$candidate" ]]; then
      UNITY_BIN="$candidate"
      return
    fi
  done

  die "Could not find Unity. Pass --unity PATH or set UNITY_PATH."
}

detect_mlagents_learn() {
  if [[ -n "$MLAGENTS_LEARN_OVERRIDE" ]]; then
    [[ -x "$MLAGENTS_LEARN_OVERRIDE" ]] || die "mlagents-learn is not runnable: $MLAGENTS_LEARN_OVERRIDE"
    LEARN_CMD=("$MLAGENTS_LEARN_OVERRIDE")
    return
  fi

  if [[ -x "$PROJECT_ROOT/.venv/bin/mlagents-learn" ]]; then
    LEARN_CMD=("$PROJECT_ROOT/.venv/bin/mlagents-learn")
    return
  fi

  if command -v mlagents-learn >/dev/null 2>&1; then
    LEARN_CMD=("$(command -v mlagents-learn)")
    return
  fi

  local python_bin=""
  if command -v python3 >/dev/null 2>&1; then
    python_bin="$(command -v python3)"
  elif command -v python >/dev/null 2>&1; then
    python_bin="$(command -v python)"
  fi

  [[ -n "$python_bin" ]] || die "Could not find Python or mlagents-learn."

  export PYTHONPATH="$PROJECT_ROOT/ml-agents/ml-agents:$PROJECT_ROOT/ml-agents/ml-agents-envs:${PYTHONPATH:-}"
  if "$python_bin" -c 'import mlagents.trainers.learn' >/dev/null 2>&1; then
    LEARN_CMD=("$python_bin" "-m" "mlagents.trainers.learn")
    return
  fi

  die "Could not import mlagents.trainers.learn. Install the local ML-Agents packages from README.md first."
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --build-name)
        [[ $# -ge 2 ]] || die "--build-name requires a value"
        BUILD_NAME="$2"
        shift 2
        ;;
      --config)
        [[ $# -ge 2 ]] || die "--config requires a value"
        CONFIGS+=("$2")
        shift 2
        ;;
      --configs)
        [[ $# -ge 2 ]] || die "--configs requires a value"
        IFS=',' read -r -a CONFIGS <<< "$2"
        shift 2
        ;;
      --base-port)
        [[ $# -ge 2 ]] || die "--base-port requires a value"
        BASE_PORT="$2"
        shift 2
        ;;
      --port-stride)
        [[ $# -ge 2 ]] || die "--port-stride requires a value"
        PORT_STRIDE="$2"
        shift 2
        ;;
      --strict-ports)
        STRICT_PORTS=1
        shift
        ;;
      --build-target)
        [[ $# -ge 2 ]] || die "--build-target requires a value"
        BUILD_TARGET="$2"
        shift 2
        ;;
      --build-output-dir)
        [[ $# -ge 2 ]] || die "--build-output-dir requires a value"
        BUILD_OUTPUT_DIR="$(abs_path "$2")"
        shift 2
        ;;
      --results-dir)
        [[ $# -ge 2 ]] || die "--results-dir requires a value"
        RESULTS_DIR="$(abs_path "$2")"
        shift 2
        ;;
      --logs-dir)
        [[ $# -ge 2 ]] || die "--logs-dir requires a value"
        LOG_ROOT="$(abs_path "$2")"
        shift 2
        ;;
      --unity)
        [[ $# -ge 2 ]] || die "--unity requires a value"
        UNITY_BIN="$2"
        shift 2
        ;;
      --mlagents-learn)
        [[ $# -ge 2 ]] || die "--mlagents-learn requires a value"
        MLAGENTS_LEARN_OVERRIDE="$2"
        shift 2
        ;;
      --no-build)
        NO_BUILD=1
        shift
        ;;
      --graphics)
        NO_GRAPHICS=0
        shift
        ;;
      --force)
        FORCE=1
        shift
        ;;
      --resume)
        RESUME=1
        shift
        ;;
      --help|-h)
        usage
        exit 0
        ;;
      --)
        shift
        EXTRA_MLAGENTS_ARGS+=("$@")
        break
        ;;
      -*)
        die "Unknown option: $1"
        ;;
      *)
        if [[ -z "$BUILD_NAME" ]]; then
          BUILD_NAME="$1"
        else
          CONFIGS+=("$1")
        fi
        shift
        ;;
    esac
  done
}

stop_children() {
  if ((${#CHILD_PIDS[@]} == 0)); then
    return
  fi

  log "Stopping ${#CHILD_PIDS[@]} training process(es)."
  local pid
  for pid in "${CHILD_PIDS[@]}"; do
    kill "$pid" >/dev/null 2>&1 || true
  done
}

build_player() {
  local build_path="$1"
  local build_log="$PROJECT_ROOT/Logs/build-$(slugify "$BUILD_NAME").log"

  detect_unity
  mkdir -p "$BUILD_OUTPUT_DIR" "$(dirname "$build_log")"
  log "Building '$BUILD_NAME' at $build_path"

  if ! "$UNITY_BIN" \
    -batchmode \
    -quit \
    -projectPath "$PROJECT_ROOT" \
    -executeMethod CommandLineBuild.BuildStandalone \
    -customBuildTarget "$BUILD_TARGET" \
    -buildName "$BUILD_NAME" \
    -buildOutputDir "$BUILD_OUTPUT_DIR" \
    -logFile "$build_log"; then
    tail -n 80 "$build_log" >&2 || true
    die "Unity build failed. Full log: $build_log"
  fi

  [[ -e "$build_path" ]] || die "Unity finished, but expected build was not found: $build_path"
}

start_training_runs() {
  local build_path="$1"
  local run_prefix="$2"
  local run_log_dir="$LOG_ROOT/$run_prefix"
  local index=0
  local config config_path config_name run_id port log_file

  mkdir -p "$RESULTS_DIR" "$run_log_dir"

  for config in "${CONFIGS[@]}"; do
    config_path="$(abs_path "$config")"
    [[ -f "$config_path" ]] || die "Config file does not exist: $config_path"

    config_name="$(slugify "$(basename "$config_path" .yaml)")"
    run_id="$run_prefix-$config_name"
    port=$((BASE_PORT + index * PORT_STRIDE))
    log_file="$run_log_dir/$run_id.log"

    local cmd=(
      "${LEARN_CMD[@]}"
      "$config_path"
      "--env=$build_path"
      "--run-id=$run_id"
      "--results-dir=$RESULTS_DIR"
      "--base-port=$port"
    )

    if ((NO_GRAPHICS)); then
      cmd+=("--no-graphics")
    fi
    if ((FORCE)); then
      cmd+=("--force")
    fi
    if ((RESUME)); then
      cmd+=("--resume")
    fi
    if ((${#EXTRA_MLAGENTS_ARGS[@]})); then
      cmd+=("${EXTRA_MLAGENTS_ARGS[@]}")
    fi

    {
      printf 'Command:'
      printf ' %q' "${cmd[@]}"
      printf '\n\n'
    } > "$log_file"

    log "Starting $run_id on port $port"
    "${cmd[@]}" >> "$log_file" 2>&1 &
    CHILD_PIDS+=("$!")
    index=$((index + 1))
  done

  log "Started ${#CHILD_PIDS[@]} training run(s). Logs: $run_log_dir"

  local failed=0
  local pid status
  for index in "${!CHILD_PIDS[@]}"; do
    pid="${CHILD_PIDS[$index]}"
    if wait "$pid"; then
      log "Finished ${CONFIGS[$index]}"
    else
      status=$?
      log "Run failed with status $status: ${CONFIGS[$index]}"
      failed=1
    fi
  done

  CHILD_PIDS=()
  if ((failed)); then
    die "One or more training runs failed. Check logs in $run_log_dir."
  fi
}

main() {
  parse_args "$@"

  [[ -n "$BUILD_NAME" ]] || die "Missing BUILD_NAME. See --help."
  [[ "$BUILD_NAME" != */* && "$BUILD_NAME" != *\\* ]] || die "Build name cannot contain path separators."
  [[ "$BASE_PORT" =~ ^[0-9]+$ ]] || die "--base-port must be an integer."
  [[ "$PORT_STRIDE" =~ ^[0-9]+$ && "$PORT_STRIDE" -gt 0 ]] || die "--port-stride must be a positive integer."

  if ((${#CONFIGS[@]} == 0)); then
    CONFIGS=("${DEFAULT_CONFIGS[@]}")
  fi

  BUILD_TARGET="$(normalize_build_target "$BUILD_TARGET")"
  local run_prefix
  run_prefix="$(slugify "$BUILD_NAME")"
  [[ -n "$run_prefix" ]] || die "Build name must contain at least one letter or number."

  local build_path
  build_path="$(player_path "$BUILD_OUTPUT_DIR" "$BUILD_NAME" "$BUILD_TARGET")"
  BASE_PORT="$(select_base_port "${#CONFIGS[@]}")"

  if ((NO_BUILD)); then
    [[ -e "$build_path" ]] || die "--no-build was passed, but this build does not exist: $build_path"
    log "Reusing existing build: $build_path"
  else
    build_player "$build_path"
  fi

  detect_mlagents_learn
  log "Using ML-Agents command: ${LEARN_CMD[*]}"
  BASE_PORT="$(select_base_port "${#CONFIGS[@]}")"

  trap stop_children INT TERM
  start_training_runs "$build_path" "$run_prefix"
  trap - INT TERM
}

main "$@"
