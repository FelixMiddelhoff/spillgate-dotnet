#!/usr/bin/env bash
# Installs the packed packages from a local feed into a throw-away console project and runs them.
# Usage: scripts/consumer-smoke.sh <directory with the .nupkg files>
set -euo pipefail
feed="$(cd "${1:?directory with the packed packages}" && pwd)"
listing="$feed"
if command -v cygpath >/dev/null 2>&1; then feed="$(cygpath -m "$feed")"; fi
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
version="$(ls "$listing"/Spillgate.0*.nupkg | sed -E 's/.*Spillgate\.([0-9][^/]*)\.nupkg/\1/' | head -1)"
cd "$work"
cat > nuget.config <<EOF
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
  </packageSources>
</configuration>
EOF
cat > smoke.csproj <<EOF
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RestoreSources>$feed</RestoreSources>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Spillgate" Version="$version" />
    <PackageReference Include="Spillgate.Brotli" Version="$version" />
    <PackageReference Include="Spillgate.Zstd" Version="$version" />
    <PackageReference Include="Spillgate.Http" Version="$version" />
  </ItemGroup>
</Project>
EOF
cat > Program.cs <<'EOF'
using System.IO.Compression;
using Spillgate;
using Spillgate.Brotli;
using Spillgate.Zstd;

var decoder = Decoder.CreateBuilder(Limits.Create(1000)).AddBrotli().AddZstd().Build();
var sink = new MemoryStream();
using (var z = new GZipStream(sink, CompressionLevel.Optimal, leaveOpen: true)) { z.Write(new byte[5000]); }
try
{
    decoder.DecodeAll(new MemoryStream(sink.ToArray()), "gzip");
    Console.WriteLine("limit not enforced");
    return 1;
}
catch (SpillgateException e) when (e.Kind == ErrorKind.OutputLimit)
{
    Console.WriteLine("ok " + e.Kind.SpecName());
}

var ok = Decoder.Create(Limits.Create(100000)).DecodeAll(new MemoryStream(sink.ToArray()), "gzip");
return ok.Length == 5000 ? 0 : 1;
EOF
dotnet run --configuration Release
