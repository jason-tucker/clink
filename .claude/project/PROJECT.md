# Clink

Windows replay-buffer clipper. .NET 10 WPF app using Windows Graphics Capture,
D3D11 and Media Foundation for capture/encode; clips are MP4 (clk1 brand).

## Build
- `.\build.ps1 -Configuration Debug`
- `dotnet` may not be on PATH; fallback `%USERPROFILE%\.dotnet\dotnet.exe`
- No .sln file.

## Tests
- In-exe: `Clink.exe --unittest [filter]`
- Env: `CLINK_TESTS_NO_SKIP=1` (fail on skipped), `CLINK_EMULATE_ELST=1` (edit-list emulation)

## Misc
- Version: `<Version>` in `Clink/Clink.csproj` (currently 0.2.6)
- CI: `.github/workflows/build.yml`
- Remote push policy: manual (ask before any push)
