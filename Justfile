rid := "osx-arm64"
aot_bin := "src/Fugue.Cli.Aot/bin/Release/net10.0/" + rid + "/publish/fugue-aot"
jit_bin  := "src/Fugue.Cli/bin/Release/net10.0/" + rid + "/publish/fugue"

# List available recipes
default:
    @just --list

# Restore NuGet packages
restore:
    dotnet restore

# Build all projects (TreatWarningsAsErrors)
build: restore
    dotnet build -c Release --no-restore

# Run all tests
test: build
    dotnet test -c Release --no-build

# Run tests without rebuild (fast iteration)
test-fast:
    dotnet test tests/Fugue.Tests/Fugue.Tests.fsproj -q

# Run tests with coverage report
cover: build
    dotnet test -c Release --no-build --collect:"XPlat Code Coverage"

# Publish JIT+ReadyToRun interactive binary
publish-jit: build
    dotnet publish src/Fugue.Cli -c Release -r {{rid}} --no-restore
    @echo "Binary: {{jit_bin}}"

# Publish Native AOT headless binary
publish-aot: build
    dotnet publish src/Fugue.Cli.Aot -c Release -r {{rid}} --no-restore
    @echo "Binary: {{aot_bin}}"

# Publish both binaries
publish: publish-jit publish-aot

# Build JIT binary and launch interactive REPL
run: publish-jit
    {{jit_bin}}

# Build AOT binary and launch it
run-aot: publish-aot
    {{aot_bin}}

# Smoke-test the AOT binary
smoke-aot: publish-aot
    {{aot_bin}} --version
    {{aot_bin}} --help

# Smoke-test the JIT binary
smoke-jit: publish-jit
    {{jit_bin}} --version

# Full CI-equivalent run: build + test + both publishes + AOT smoke
ci: build test publish smoke-aot

# Remove all build artifacts
clean:
    dotnet clean
    find . -type d \( -name bin -o -name obj \) -not -path '*/.git/*' -exec rm -rf {} + 2>/dev/null || true
