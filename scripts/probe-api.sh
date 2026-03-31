#!/usr/bin/env bash
# probe-api.sh — Compile a C# snippet against Jellyfin 10.11.6 to verify API signatures.
#
# Usage:
#   ./scripts/probe-api.sh
#   # Edit /tmp/JellyfinProbe/Program.cs, then re-run
#
# The probe project auto-references Jellyfin.Controller, Jellyfin.Model, and
# Jellyfin.Database.Implementations at the same versions used by the plugin.
# Useful for checking method signatures, available properties, and namespace locations
# without manually decompiling DLLs.
#
# Example Program.cs to check IPlaylistManager.AddItemToPlaylistAsync:
#   using MediaBrowser.Controller.Playlists;
#   using System;
#   IPlaylistManager pm = null!;
#   await pm.AddItemToPlaylistAsync(Guid.Empty, new Guid[0], Guid.Empty);
#   // If it compiles, the signature is correct.
#   // If it fails with CS1503, read the error to see the expected type.

set -euo pipefail

PROBE_DIR="/tmp/JellyfinProbe"

# Create or reset the probe project
mkdir -p "$PROBE_DIR"
cat > "$PROBE_DIR/JellyfinProbe.csproj" << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="10.11.6" />
    <PackageReference Include="Jellyfin.Model" Version="10.11.6" />
    <PackageReference Include="Jellyfin.Database.Implementations" Version="10.11.6" />
  </ItemGroup>
</Project>
EOF

if [ ! -f "$PROBE_DIR/Program.cs" ]; then
  cat > "$PROBE_DIR/Program.cs" << 'EOF'
// Edit this file to probe Jellyfin API signatures, then re-run probe-api.sh
// Example:
using MediaBrowser.Controller.Playlists;
using System;

IPlaylistManager pm = null!;
// Uncomment and modify to test signatures:
// await pm.AddItemToPlaylistAsync(Guid.Empty, new Guid[0], Guid.Empty);
Console.WriteLine("Edit /tmp/JellyfinProbe/Program.cs to probe an API.");
EOF
  echo "[probe] Created $PROBE_DIR/Program.cs — edit it and re-run."
fi

echo "[probe] Building..."
cd "$PROBE_DIR" && dotnet build --nologo -v q 2>&1
echo "[probe] Done. Edit $PROBE_DIR/Program.cs to try different signatures."
