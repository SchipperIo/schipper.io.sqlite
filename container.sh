#!/usr/bin/env bash
# Runs inside mcr.microsoft.com/dotnet/sdk:10.0. Invoked by build.sh and build.ps1.
# The repo is mounted at /workspace and copied to /tmp/build so bin/ and obj/ stay
# off the host. artifacts/ is left behind; it is large and regenerated inside /tmp.
# ../nuget.cache is mounted at /tmp/nuget.cache for Schipper.* restore.
set -euo pipefail

export HOME=/tmp NUGET_PACKAGES=/tmp/nuget DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$PATH:$HOME/.dotnet/tools"

: "${DO_TEST:=0}"
: "${DO_INTEG:=0}"
: "${DO_PACK:=0}"
: "${DO_RUN:=0}"
: "${DO_TRACE:=0}"
: "${DO_OUT:=0}"
: "${RID:=linux-x64}"

SRCDIR=/tmp/build
mkdir -p "$SRCDIR"
find /workspace -mindepth 1 -maxdepth 1 \
  ! -name artifacts ! -name .git ! -name dist \
  -exec cp -a {} "$SRCDIR/" \;
cd "$SRCDIR"

SOLUTION=Schipper.Io.Sqlite.slnx
LIB=Schipper.Io.Sqlite/src/Schipper.Io.Sqlite.csproj
TESTS=(
  Schipper.Io.Sqlite/tests/Schipper.Io.Sqlite.Tests/Schipper.Io.Sqlite.Tests.csproj
  Schipper.Io.Sqlite.Generator/tests/Schipper.Io.Sqlite.Generator.Tests/Schipper.Io.Sqlite.Generator.Tests.csproj
)

RAW=/workspace/dist/raw
TRACE=/workspace/dist/trace

run() {
  local name="$1"; shift
  if [ "$DO_OUT" = 1 ]; then
    mkdir -p "$RAW"
    "$@" 2>&1 | tee "$RAW/$name.log"
  else
    "$@"
  fi
}

echo "==> build $SOLUTION"
run build dotnet build "$SOLUTION" -c Release

if [ "$DO_TEST" = 1 ]; then
  echo "==> unit tests $SOLUTION"
  run test dotnet test --solution "$SOLUTION" -c Release
fi

if [ "$DO_INTEG" = 1 ]; then
  echo "==> no integration tests, skipping -i"
fi

if [ "$DO_TRACE" = 1 ]; then
  echo "==> traced unit tests -> dist/trace"
  mkdir -p "$TRACE"
  dotnet tool install --global dotnet-trace >/dev/null 2>&1 || true
  for TEST in "${TESTS[@]}"; do
    NAME=$(basename "$TEST" .csproj)
    dotnet build "$TEST" -c Release
    TESTDLL=$(find "$SRCDIR/artifacts" -name "$NAME.dll" -not -path "*/ref/*" -not -path "*/refint/*" | head -n1 || true)
    if [ -z "$TESTDLL" ]; then
      echo "==> $NAME.dll not found, skipping its trace" >&2
      continue
    fi
    NETTRACE="$TRACE/$NAME.nettrace"
    run "trace-$NAME" dotnet trace collect --output "$NETTRACE" -- dotnet exec "$TESTDLL"
    echo "==> trace report topN -> dist/trace/$NAME.topN.txt"
    dotnet trace report "$NETTRACE" topN -n 50 > "$TRACE/$NAME.topN.txt"
  done
fi

if [ "$DO_PACK" = 1 ]; then
  echo "==> pack -> dist/"
  mkdir -p /workspace/dist
  run pack dotnet pack "$LIB" -c Release -o /workspace/dist
fi

if [ "$DO_RUN" = 1 ]; then
  echo "==> Schipper.Io.Sqlite is a library. Publish the Native AOT bench locally; skipping -r"
fi
