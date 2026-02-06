#!/bin/bash
# Build and package Palco plugin for Jellyfin
# Usage: ./build.sh [version]
# If version is provided, updates all version references

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Paths
DOTNET_PATH="${HOME}/.dotnet/dotnet"
PUBLISH_DIR="$SCRIPT_DIR/publish"
OUTPUT_DIR="$SCRIPT_DIR/release"

# Check if dotnet exists
if [ ! -f "$DOTNET_PATH" ]; then
    echo "Error: dotnet not found at $DOTNET_PATH"
    exit 1
fi

# Get current version from csproj
CURRENT_VERSION=$(grep -oP '<Version>\K[^<]+' Palco.csproj)
echo "Current version: $CURRENT_VERSION"

# If a new version is provided, update all version references
if [ -n "$1" ]; then
    NEW_VERSION="$1"
    echo "Updating version to: $NEW_VERSION"
    
    # Update Palco.csproj
    sed -i "s/<AssemblyVersion>.*<\/AssemblyVersion>/<AssemblyVersion>$NEW_VERSION<\/AssemblyVersion>/" Palco.csproj
    sed -i "s/<FileVersion>.*<\/FileVersion>/<FileVersion>$NEW_VERSION<\/FileVersion>/" Palco.csproj
    sed -i "s/<Version>.*<\/Version>/<Version>$NEW_VERSION<\/Version>/" Palco.csproj
    
    # Update meta.json
    sed -i "s/\"version\": \".*\"/\"version\": \"$NEW_VERSION\"/" meta.json
    
    # Update build.yaml
    sed -i "s/version: \".*\"/version: \"$NEW_VERSION\"/" build.yaml
    
    CURRENT_VERSION="$NEW_VERSION"
fi

echo "Building Palco v$CURRENT_VERSION..."

# Clean previous builds
rm -rf "$PUBLISH_DIR"
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Build the project
"$DOTNET_PATH" publish Palco.csproj \
    -c Release \
    -o "$PUBLISH_DIR" \
    --self-contained false \
    /p:DebugType=None \
    /p:DebugSymbols=false

# Ensure meta.json is in publish folder
cp meta.json "$PUBLISH_DIR/meta.json"

# Create the zip file (files at root level, no subdirectory)
ZIP_NAME="palco_${CURRENT_VERSION}.zip"
echo "Creating $ZIP_NAME..."

cd "$PUBLISH_DIR"
zip -r "$OUTPUT_DIR/$ZIP_NAME" ./*
cd "$SCRIPT_DIR"

# Calculate MD5 checksum
MD5_HASH=$(md5sum "$OUTPUT_DIR/$ZIP_NAME" | cut -d' ' -f1)
echo "MD5: $MD5_HASH"

# Generate timestamp
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Print manifest entry for easy copy-paste
echo ""
echo "========================================"
echo "Build complete!"
echo "========================================"
echo "Zip: $OUTPUT_DIR/$ZIP_NAME"
echo "MD5: $MD5_HASH"
echo ""
echo "Add this to manifest.json versions array:"
echo ""
cat << EOF
{
  "version": "$CURRENT_VERSION",
  "changelog": "YOUR CHANGELOG HERE",
  "targetAbi": "10.11.0.0",
  "sourceUrl": "https://github.com/j4ckgrey/Palco/releases/download/v$CURRENT_VERSION/palco_${CURRENT_VERSION}.zip",
  "checksum": "$MD5_HASH",
  "timestamp": "$TIMESTAMP"
}
EOF
echo ""
echo "========================================"
echo "After uploading to GitHub release, update manifest.json and push!"
