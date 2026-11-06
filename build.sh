#!/usr/bin/env bash
# macOS/Linux build for AquaMai. Mirrors build.ps1:
# restore packages, generate AquaMai/BuildInfo.g.cs, then build the solution.

set -euo pipefail

CONFIGURATION="Release"
SOURCE_ROOT=""

usage() {
    cat <<'EOF'
Usage: ./build.sh [-c|--configuration Release|Debug] [--source-root PATH]

Builds AquaMai.slnx and writes Output/AquaMai.dll.

Dependencies:
  .NET SDK          brew install dotnet
  Libs/AMDaemon.NET.dll and Libs/Assembly-CSharp.dll
                    copied from a Sinmai install, or downloaded automatically
                    from the CI reference archive when they are missing
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration|-Configuration)
            CONFIGURATION="${2:?configuration value required}"
            shift 2
            ;;
        --source-root|-SourceRoot)
            SOURCE_ROOT="${2:?source root path required}"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        Release|Debug)
            CONFIGURATION="$1"
            shift
            ;;
        *)
            echo "error: unknown argument: $1" >&2
            usage >&2
            exit 1
            ;;
    esac
done

if [[ -z "$SOURCE_ROOT" ]]; then
    SOURCE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fi
cd "$SOURCE_ROOT"

export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Homebrew's dotnet formula does not set this for child processes.
if [[ -z "${DOTNET_ROOT:-}" && -d "/opt/homebrew/opt/dotnet/libexec" ]]; then
    export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
elif [[ -z "${DOTNET_ROOT:-}" && -d "/usr/local/opt/dotnet/libexec" ]]; then
    export DOTNET_ROOT="/usr/local/opt/dotnet/libexec"
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: the .NET SDK was not found on PATH." >&2
    echo "Install it with: brew install dotnet" >&2
    exit 1
fi

REFERENCE_ARCHIVE_URL="https://res.mumur.net/aquamai-ci-reference.zip"
REFERENCE_ARCHIVE_SHA256="4a3ef389ce03f8400822cf5b749ff97a535b0ff709b757df4770cbb558335c22"

ensure_reference_assemblies() {
    local missing=()
    local dll
    for dll in AMDaemon.NET.dll Assembly-CSharp.dll; do
        if [[ ! -f "Libs/$dll" ]]; then
            missing+=("$dll")
        fi
    done
    if [[ ${#missing[@]} -eq 0 ]]; then
        return 0
    fi

    echo "Missing reference assemblies: ${missing[*]}"
    echo "Downloading CI reference archive..."
    local tmp archive actual
    tmp="$(mktemp -d)"
    archive="$tmp/aquamai-ci-reference.zip"
    if ! curl -fsSL "$REFERENCE_ARCHIVE_URL" -o "$archive"; then
        echo "error: failed to download $REFERENCE_ARCHIVE_URL" >&2
        echo "Copy AMDaemon.NET.dll and Assembly-CSharp.dll into Libs/ from a Sinmai install." >&2
        rm -rf "$tmp"
        exit 1
    fi

    actual="$(shasum -a 256 "$archive" | awk '{print $1}')"
    if [[ "$actual" != "$REFERENCE_ARCHIVE_SHA256" ]]; then
        echo "error: reference archive SHA256 mismatch." >&2
        echo "  expected $REFERENCE_ARCHIVE_SHA256" >&2
        echo "  got      $actual" >&2
        rm -rf "$tmp"
        exit 1
    fi

    unzip -q -o "$archive" -d "Libs"
    rm -rf "$tmp"

    for dll in "${missing[@]}"; do
        if [[ ! -f "Libs/$dll" ]]; then
            echo "error: archive did not contain $dll" >&2
            exit 1
        fi
    done
}

csharp_escape() {
    # Escape a value so it is safe inside a C# string literal.
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    value="${value//$'\n'/}"
    value="${value//$'\r'/}"
    printf '%s' "$value"
}

generate_build_info() {
    local raw=""
    if ! raw="$(git describe --tags 2>/dev/null)"; then
        local short_hash
        short_hash="$(git rev-parse --short=12 HEAD)"
        echo "warning: git describe --tags failed (this clone has no tags). Using 0.0.0-g${short_hash}." >&2
        raw="0.0.0-0-g${short_hash}"
    fi

    if [[ "$raw" == v* ]]; then
        raw="${raw#v}"
    fi

    # Match build.ps1: split on every '-' and treat [0]=tag, [1]=count, [2]=hash.
    local tag_version commit_count hash short_ver git_describe
    local -a describe_parts=()
    IFS='-' read -r -a describe_parts <<< "$raw"
    tag_version="${describe_parts[0]}"

    if [[ ${#describe_parts[@]} -ge 3 ]]; then
        commit_count="${describe_parts[1]}"
        hash="${describe_parts[2]}"
        local -a ver_parts=()
        IFS='.' read -r -a ver_parts <<< "$tag_version"
        while [[ ${#ver_parts[@]} -lt 3 ]]; do
            ver_parts+=("0")
        done
        ver_parts[2]="$commit_count"
        local IFS='.'
        short_ver="${ver_parts[*]}"
        git_describe="${short_ver}-${hash}"
    else
        short_ver="$tag_version"
        git_describe="$raw"
    fi

    local branch
    branch="$(git rev-parse --abbrev-ref HEAD)"
    if [[ "$branch" != "main" ]]; then
        git_describe="${git_describe}-${branch}"
    fi

    if [[ -z "${CI:-}" && -z "${GITHUB_ACTIONS:-}" ]]; then
        if [[ -n "$(git status --porcelain)" ]]; then
            git_describe="${git_describe}-dirty"
        fi
    fi

    # AssemblyVersion / AssemblyFileVersion require numeric major.minor.build[.revision].
    # Git describe suffixes are fine for GitVersion display, but the file version must stay numeric.
    local file_version="$short_ver"
    if [[ ! "$file_version" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
        echo "warning: version '$file_version' is not a numeric assembly version. Using 0.0.0." >&2
        file_version="0.0.0"
        if [[ "$short_ver" == "$git_describe" ]]; then
            git_describe="${file_version}-${git_describe}"
        fi
        short_ver="$file_version"
    fi

    local build_date
    build_date="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

    short_ver="$(csharp_escape "$short_ver")"
    git_describe="$(csharp_escape "$git_describe")"
    build_date="$(csharp_escape "$build_date")"

    cat > "./AquaMai/BuildInfo.g.cs" <<EOF
// Auto-generated file. Do not modify manually.
namespace AquaMai;

public static partial class BuildInfo
{
    public const string Version = "${short_ver}";
    public const string GitVersion = "${git_describe}";
    public const string BuildDate = "${build_date}";
}
EOF
}

ensure_reference_assemblies

echo "Restoring packages..."
dotnet restore "./AquaMai.slnx"

echo "Generating BuildInfo..."
generate_build_info

echo "Building solution ($CONFIGURATION)..."
dotnet build "./AquaMai.slnx" -c "$CONFIGURATION"
