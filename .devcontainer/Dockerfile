# Same floating .NET 10 SDK image as build.sh and CI. global.json rolls forward to the latest
# 10.0 minor, so this tag stays valid as the image moves.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# clang and zlib: the Native AOT toolchain. Schipper.Io.Sqlite.Bench sets PublishAot, and ILC shells
# out to clang to link. `dotnet build` does not need these; `dotnet publish` and the benchmark
# suite fail without them.
#
# sqlite3: for inspecting databases the tests and bench leave behind.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        clang \
        zlib1g-dev \
        sqlite3 \
    && apt-get clean
