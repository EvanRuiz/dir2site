dotnet publish -r win-x64 -p:Platform=x64 -p:Configuration=Release --self-contained

New-Item -Path "builds" -ItemType Directory -Force

# For windows build, place all build files under /bin in zip
Remove-Item -Force -Recurse -Path "bin/x64/Release/net9.0/win-x64/bin"
Move-Item -Force -Path "bin/x64/Release/net9.0/win-x64/publish" -Destination "bin/x64/Release/net9.0/win-x64/bin"

Compress-Archive -Force -Path "bin/x64/Release/net9.0/win-x64/bin" -DestinationPath "builds/dir2site-win-x64.zip"

# Convenience bat for quickly running after unzip with build files encapsulated under the bin folder
# TODO: When/if official installer, this won't be necessary
Compress-Archive -Update -Path "dir2site.bat" -DestinationPath "builds/dir2site-win-x64.zip"



