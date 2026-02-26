#!/usr/bin/env bash
set -euo pipefail

INSTALL_ROOT="${INSTALL_ROOT:-$HOME/.local/share/gitcopilot}"
AOT="false"
FORCE_AOT="false"
RUNTIME="$(uname -m)"

case "$(uname -s)" in
  Linux)
    if [[ "$RUNTIME" == "x86_64" ]]; then RUNTIME="linux-x64"; fi
    ;;
  Darwin)
    if [[ "$RUNTIME" == "x86_64" ]]; then RUNTIME="osx-x64"; fi
    if [[ "$RUNTIME" == "arm64" ]]; then RUNTIME="osx-arm64"; fi
    ;;
esac

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install-root)
      INSTALL_ROOT="$2"
      shift 2
      ;;
    --aot)
      AOT="true"
      shift
      ;;
    --force-aot)
      FORCE_AOT="true"
      shift
      ;;
    --runtime)
      RUNTIME="$2"
      shift 2
      ;;
    *)
      echo "Unknown option: $1" >&2
      echo "Usage: $0 [--install-root <path>] [--aot] [--force-aot] [--runtime <rid>]" >&2
      exit 1
      ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SOURCE_FILE="$REPO_ROOT/copilot.cs"

if [[ ! -f "$SOURCE_FILE" ]]; then
  echo "Could not find copilot.cs at $SOURCE_FILE" >&2
  exit 1
fi

PUBLISH_DIR="$INSTALL_ROOT/app"
BIN_DIR="$HOME/.local/bin"
TARGET_BIN="$BIN_DIR/gitcopilot"

mkdir -p "$PUBLISH_DIR" "$BIN_DIR"

echo "Building gitcopilot (Release)..."
dotnet build "$SOURCE_FILE" -c Release

publish_succeeded="false"

if [[ "$AOT" == "true" ]]; then
  echo "Publishing gitcopilot (Release + AOT, Runtime=$RUNTIME)..."
  if dotnet publish "$SOURCE_FILE" -c Release -r "$RUNTIME" --self-contained true -o "$PUBLISH_DIR" /p:PublishAot=true /p:PublishSingleFile=true /p:InvariantGlobalization=true; then
    publish_succeeded="true"
  elif [[ "$FORCE_AOT" == "true" ]]; then
    echo "AOT publish failed and --force-aot is set" >&2
    exit 1
  else
    echo "AOT publish failed. Falling back to standard Release publish..."
  fi
fi

if [[ "$publish_succeeded" != "true" ]]; then
  echo "Publishing gitcopilot (Release)..."
  dotnet publish "$SOURCE_FILE" -c Release -o "$PUBLISH_DIR" --no-self-contained /p:PublishAot=false /p:PublishSingleFile=false /p:SelfContained=false /p:RuntimeIdentifier=
fi

PUBLISHED_BIN="$PUBLISH_DIR/copilot"
if [[ ! -x "$PUBLISHED_BIN" ]]; then
  CANDIDATE="$(find "$PUBLISH_DIR" -maxdepth 1 -type f -perm -u+x | head -n 1 || true)"
  if [[ -z "$CANDIDATE" ]]; then
    echo "No executable produced in $PUBLISH_DIR" >&2
    exit 1
  fi
  PUBLISHED_BIN="$CANDIDATE"
fi

cat > "$TARGET_BIN" <<EOF
#!/usr/bin/env bash
set -euo pipefail
if [[ \$# -eq 0 ]]; then
  exec "$PUBLISHED_BIN" --repl
else
  exec "$PUBLISHED_BIN" "\$@"
fi
EOF

chmod +x "$TARGET_BIN"

SHELL_RC=""
if [[ -n "${ZSH_VERSION:-}" ]]; then
  SHELL_RC="$HOME/.zshrc"
else
  SHELL_RC="$HOME/.bashrc"
fi

PATH_EXPORT='export PATH="$HOME/.local/bin:$PATH"'
if [[ -f "$SHELL_RC" ]]; then
  if ! grep -Fq "$PATH_EXPORT" "$SHELL_RC"; then
    echo "" >> "$SHELL_RC"
    echo "$PATH_EXPORT" >> "$SHELL_RC"
    echo "Added ~/.local/bin to PATH in $SHELL_RC"
  else
    echo "PATH already configured in $SHELL_RC"
  fi
else
  echo "$PATH_EXPORT" > "$SHELL_RC"
  echo "Created $SHELL_RC and added PATH export"
fi

echo ""
echo "Install complete."
echo "Run in a new shell: gitcopilot"
echo "Optional one-shot mode: gitcopilot --profile cloud \"summarize this diff\""

echo ""
echo "Running post-install self-check..."
if "$TARGET_BIN" --help >/dev/null 2>&1; then
  echo "Self-check passed: gitcopilot launcher is executable."
else
  echo "Self-check failed: launcher execution failed" >&2
  exit 1
fi
