function New-ReleaseZip {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )

    Remove-Item $DestinationPath -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $SourcePath '*') -DestinationPath $DestinationPath -Force
}

function Copy-DirectoryContentsExcluding {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [string[]]$ExcludedRelativePaths
    )

    New-Item -Path $DestinationPath -ItemType "directory" -Force | Out-Null

    $SourceFullPath = (Resolve-Path $SourcePath).Path
    $Excluded = @{}
    foreach ($Path in $ExcludedRelativePaths) {
        $Excluded[$Path.Replace('\', '/')] = $true
    }

    Get-ChildItem -Path $SourceFullPath -Recurse -Force | ForEach-Object {
        $RelativePath = $_.FullName.Substring($SourceFullPath.Length).TrimStart('\', '/').Replace('\', '/')

        if ($Excluded.ContainsKey($RelativePath)) {
            return
        }

        $TargetPath = Join-Path $DestinationPath $RelativePath
        if ($_.PSIsContainer) {
            New-Item -Path $TargetPath -ItemType "directory" -Force | Out-Null
        }
        else {
            $TargetDir = Split-Path $TargetPath -Parent
            New-Item -Path $TargetDir -ItemType "directory" -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $TargetPath -Force
        }
    }
}

function Build-EditorPackage {
    $StandalonePath = "Release/UnityExplorer.Standalone.Mono"
    $EditorStagePath = "Release/UnityExplorer.Editor.Package"
    $EditorRuntimePath = Join-Path $EditorStagePath "Runtime"

    Remove-Item $EditorStagePath -Recurse -Force -ErrorAction SilentlyContinue
    Copy-DirectoryContentsExcluding -SourcePath "UnityEditorPackage" -DestinationPath $EditorStagePath -ExcludedRelativePaths @(
        "Runtime/UnityExplorer.STANDALONE.Mono.dll",
        "Runtime/UniverseLib.Mono.dll"
    )

    Copy-Item -Path "$StandalonePath/UnityExplorer.STANDALONE.Mono.dll" -Destination $EditorRuntimePath -Force
    Copy-Item -Path "$StandalonePath/UniverseLib.Mono.dll" -Destination $EditorRuntimePath -Force

    New-ReleaseZip -SourcePath $EditorStagePath -DestinationPath "Release/UnityExplorer.Editor.zip"
}

function Build-McpSidecar {
    $SidecarPath = "sidecar"
    $SidecarReleasePath = "Release/UnityExplorer.MCP.Sidecar"

    Push-Location $SidecarPath
    try {
        New-Item -Path "../Release" -ItemType "directory" -Force | Out-Null

        if (Test-Path "package-lock.json") {
            npm ci
        }
        else {
            npm install
        }

        npm run build
        npm pack --pack-destination "../Release"
    }
    finally {
        Pop-Location
    }

    Remove-Item $SidecarReleasePath -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -Path $SidecarReleasePath -ItemType "directory" -Force
    Copy-Item -Path "sidecar/package.json" -Destination $SidecarReleasePath -Force
    Copy-Item -Path "sidecar/package-lock.json" -Destination $SidecarReleasePath -Force
    Copy-Item -Path "sidecar/USAGE.md" -Destination $SidecarReleasePath -Force
    Copy-Item -Path "sidecar/dist" -Destination "$SidecarReleasePath/dist" -Recurse -Force

    New-ReleaseZip -SourcePath $SidecarReleasePath -DestinationPath "Release/UnityExplorer.MCP.Sidecar.zip"
}

# ----------- MelonLoader IL2CPP (net6) -----------
dotnet build src/UnityExplorer.sln -c Release_ML_Cpp_net6
$Path = "Release\UnityExplorer.MelonLoader.IL2CPP.net6preview"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net6 /lib:lib/unhollowed /lib:$Path /internalize /out:$Path/UnityExplorer.ML.IL2CPP.net6preview.dll $Path/UnityExplorer.ML.IL2CPP.net6preview.dll $Path/mcs.dll 
# (cleanup and move files)
Remove-Item $Path/UnityExplorer.ML.IL2CPP.net6preview.deps.json
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/UnhollowerBaseLib.dll
New-Item -Path "$Path" -Name "Mods" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.ML.IL2CPP.net6preview.dll -Destination $Path/Mods -Force
New-Item -Path "$Path" -Name "UserLibs" -ItemType "directory" -Force
Move-Item -Path $Path/UniverseLib.IL2CPP.Unhollower.dll -Destination $Path/UserLibs -Force
# (create zip archive)
New-ReleaseZip -SourcePath $Path -DestinationPath "$Path/../UnityExplorer.MelonLoader.IL2CPP.net6preview.zip"

# ----------- MelonLoader IL2CPP (net472) -----------
dotnet build src/UnityExplorer.sln -c Release_ML_Cpp_net472
$Path = "Release/UnityExplorer.MelonLoader.IL2CPP"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net472 /lib:lib/net35 /lib:lib/unhollowed /lib:$Path /internalize /out:$Path/UnityExplorer.ML.IL2CPP.dll $Path/UnityExplorer.ML.IL2CPP.dll $Path/mcs.dll 
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/UnhollowerBaseLib.dll
New-Item -Path "$Path" -Name "Mods" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.ML.IL2CPP.dll -Destination $Path/Mods -Force
New-Item -Path "$Path" -Name "UserLibs" -ItemType "directory" -Force
Move-Item -Path $Path/UniverseLib.IL2CPP.Unhollower.dll -Destination $Path/UserLibs -Force
# (create zip archive)
New-ReleaseZip -SourcePath $Path -DestinationPath "$Path/../UnityExplorer.MelonLoader.IL2CPP.zip"

# ----------- MelonLoader Mono -----------
dotnet build src/UnityExplorer.sln -c Release_ML_Mono
$Path = "Release/UnityExplorer.MelonLoader.Mono"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$Path /internalize /out:$Path/UnityExplorer.ML.Mono.dll $Path/UnityExplorer.ML.Mono.dll $Path/mcs.dll 
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
New-Item -Path "$Path" -Name "Mods" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.ML.Mono.dll -Destination $Path/Mods -Force
New-Item -Path "$Path" -Name "UserLibs" -ItemType "directory" -Force
Move-Item -Path $Path/UniverseLib.Mono.dll -Destination $Path/UserLibs -Force
# (create zip archive)
New-ReleaseZip -SourcePath $Path -DestinationPath "$Path/../UnityExplorer.MelonLoader.Mono.zip"

# ----------- BepInEx IL2CPP -----------
dotnet build src/UnityExplorer.sln -c Release_BIE_Cpp
$Path = "Release/UnityExplorer.BepInEx.IL2CPP"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net472 /lib:lib/unhollowed /lib:$Path /internalize /out:$Path/UnityExplorer.BIE.IL2CPP.dll $Path/UnityExplorer.BIE.IL2CPP.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/UnhollowerBaseLib.dll
New-Item -Path "$Path" -Name "plugins" -ItemType "directory" -Force
New-Item -Path "$Path" -Name "plugins/sinai-dev-UnityExplorer" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.BIE.IL2CPP.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
Move-Item -Path $Path/UniverseLib.IL2CPP.Unhollower.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
# (create zip archive)
New-ReleaseZip -SourcePath $Path -DestinationPath "$Path/../UnityExplorer.BepInEx.IL2CPP.zip"

# ----------- BepInEx IL2CPP CoreCLR -----------
dotnet build src/UnityExplorer.sln -c Release_BIE_CoreCLR
$Path = "Release/UnityExplorer.BepInEx.IL2CPP.CoreCLR"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net472 /lib:lib/net6/ /lib:lib/interop/ /lib:$Path /internalize /out:$Path/UnityExplorer.BIE.IL2CPP.CoreCLR.dll $Path/UnityExplorer.BIE.IL2CPP.CoreCLR.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/Il2CppInterop.Common.dll
Remove-Item $Path/Il2CppInterop.Runtime.dll
Remove-Item $Path/Microsoft.Extensions.Logging.Abstractions.dll
Remove-Item $Path/UnityExplorer.BIE.IL2CPP.CoreCLR.deps.json
New-Item -Path "$Path" -Name "plugins" -ItemType "directory" -Force
New-Item -Path "$Path" -Name "plugins/sinai-dev-UnityExplorer" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.BIE.IL2CPP.CoreCLR.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
Move-Item -Path $Path/UniverseLib.IL2CPP.Interop.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
# (create zip archive)
New-ReleaseZip -SourcePath $Path -DestinationPath "$Path/../UnityExplorer.BepInEx.IL2CPP.CoreCLR.zip"

# ----------- BepInEx 5 Mono -----------
dotnet build src/UnityExplorer.sln -c Release_BIE5_Mono
$Path = "Release/UnityExplorer.BepInEx5.Mono"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$Path /internalize /out:$Path/UnityExplorer.BIE5.Mono.dll $Path/UnityExplorer.BIE5.Mono.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
New-Item -Path "$Path" -Name "plugins" -ItemType "directory" -Force
New-Item -Path "$Path" -Name "plugins/sinai-dev-UnityExplorer" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.BIE5.Mono.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
Move-Item -Path $Path/UniverseLib.Mono.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
# (create zip archive)
New-ReleaseZip -SourcePath $Path -DestinationPath "$Path/../UnityExplorer.BepInEx5.Mono.zip"

# ----------- BepInEx 6 Mono -----------
dotnet build src/UnityExplorer.sln -c Release_BIE6_Mono
$Path = "Release/UnityExplorer.BepInEx6.Mono"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$Path /internalize /out:$Path/UnityExplorer.BIE6.Mono.dll $Path/UnityExplorer.BIE6.Mono.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
New-Item -Path "$Path" -Name "plugins" -ItemType "directory" -Force
New-Item -Path "$Path" -Name "plugins/sinai-dev-UnityExplorer" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.BIE6.Mono.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
Move-Item -Path $Path/UniverseLib.Mono.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
# (create zip archive)
New-ReleaseZip -SourcePath $Path -DestinationPath "$Path/../UnityExplorer.BepInEx6.Mono.zip"

# ----------- Standalone Mono -----------
dotnet build src/UnityExplorer.sln -c Release_STANDALONE_Mono
$Path = "Release/UnityExplorer.Standalone.Mono"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$Path /internalize /out:$Path/UnityExplorer.Standalone.Mono.dll $Path/UnityExplorer.Standalone.Mono.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
# (create zip archive)
New-ReleaseZip -SourcePath $Path -DestinationPath "$Path/../UnityExplorer.Standalone.Mono.zip"

# ----------- Standalone IL2CPP -----------
dotnet build src/UnityExplorer.sln -c Release_STANDALONE_Cpp
$Path = "Release/UnityExplorer.Standalone.IL2CPP"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net472 /lib:lib/unhollowed /lib:$Path /internalize /out:$Path/UnityExplorer.Standalone.IL2CPP.dll $Path/UnityExplorer.Standalone.IL2CPP.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/UnhollowerBaseLib.dll
# (create zip archive)
New-ReleaseZip -SourcePath $Path -DestinationPath "$Path/../UnityExplorer.Standalone.IL2CPP.zip"

# ----------- Editor (mono) -----------
Build-EditorPackage

# ----------- MCP Sidecar (Node.js) -----------
Build-McpSidecar
