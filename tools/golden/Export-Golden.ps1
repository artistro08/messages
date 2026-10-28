<#
.SYNOPSIS
    Regenerates the golden test vectors from the real LibGM (Go) code.

.DESCRIPTION
    Outputs: JSON files (one per area, e.g. smoke.json) in
    src/GMessages.Protocol.Tests/Golden/.

    Used for: making the original Go library produce the exact expected values
    the C# tests must match byte-for-byte. Running the C# tests never needs Go;
    only regenerating the vectors does.

    Steps: clone mautrix-gmessages at the commit in UPSTREAM_COMMIT into
    tools/golden/.upstream (skipped if already at that commit), copy
    golden_export_test.go into pkg/libgm, run TestExportGolden, then remove the
    copied file so the clone stays pristine.

    Depends on: Go (https://go.dev/dl/) and git. Uses `go` from PATH, falling
    back to $env:ProgramFiles\Go\bin\go.exe.
#>

$ErrorActionPreference = 'Stop'

# Paths
$golden_dir = $PSScriptRoot
$repo_root  = Split-Path (Split-Path $golden_dir -Parent) -Parent
$upstream   = Join-Path $golden_dir '.upstream'
$out_dir    = Join-Path $repo_root 'src/GMessages.Protocol.Tests/Golden'
$test_file  = 'golden_export_test.go'
$commit     = (Get-Content (Join-Path $golden_dir 'UPSTREAM_COMMIT') -Raw).Trim()

# Find Go
$go = (Get-Command go -ErrorAction SilentlyContinue)?.Source
if (-not $go) {
    $fallback = Join-Path $env:ProgramFiles 'Go\bin\go.exe'
    if (Test-Path $fallback) { $go = $fallback }
}
if (-not $go) {
    throw 'Go was not found on PATH or in Program Files. Install it from https://go.dev/dl/ and retry.'
}

# Clone Upstream Only If Missing Or At A Different Commit
$head = if (Test-Path (Join-Path $upstream '.git')) { (git -C $upstream rev-parse HEAD) } else { $null }
if ($head -ne $commit) {
    if (Test-Path $upstream) { Remove-Item $upstream -Recurse -Force }
    git clone https://github.com/mautrix/gmessages $upstream
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }
    git -C $upstream checkout $commit
    if ($LASTEXITCODE -ne 0) { throw "git checkout $commit failed." }
}

# Run Export
New-Item -ItemType Directory -Force $out_dir | Out-Null
$copied = Join-Path $upstream "pkg/libgm/$test_file"
Copy-Item (Join-Path $golden_dir $test_file) $copied -Force
try {
    $env:GOLDEN_OUT = $out_dir
    Push-Location $upstream
    try {
        & $go test -v -run TestExportGolden -count=1 ./pkg/libgm
        $exit = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    Remove-Item $copied -Force -ErrorAction SilentlyContinue
}
if ($exit -ne 0) { throw "go test failed (exit $exit)." }
