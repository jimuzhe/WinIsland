# WinIsland MSIX Packaging

This folder contains an MSIX packaging project for `WinIsland.csproj`.

## Included

- `WinIsland.Package.wapproj`: Desktop Bridge packaging project.
- `Package.appxmanifest`: App identity, visual assets, and capabilities.
- `Assets/*`: package logos (currently using `assets/ico.png` as placeholders).

## Capabilities

`Package.appxmanifest` includes:

- `runFullTrust` (required for desktop process)
- `userNotificationListener` (required to read system notifications like QQ/WeChat)

## Important

`Identity Publisher` must match your signing certificate subject.

Current value:

- `Publisher="CN=LongDz"`

If your certificate uses another CN, update `Package.appxmanifest` accordingly.

## Build (MSBuild)

Use Visual Studio Build Tools MSBuild (not `dotnet msbuild` for `.wapproj`):

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
  .\WinIsland.Package\WinIsland.Package.wapproj `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:UapAppxPackageBuildMode=SideloadOnly `
  /p:AppxBundle=Never
```

Generated MSIX output is under:

- `WinIsland.Package\AppPackages\`

## Signing

If signing is enabled, add:

```powershell
/p:AppxPackageSigningEnabled=true /p:PackageCertificateKeyFile="path\to\your.pfx" /p:PackageCertificatePassword="***"
```
