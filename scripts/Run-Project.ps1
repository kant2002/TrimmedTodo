param (
    [Parameter()]
    [string]
    $ProjectName,
    [Parameter()]
    [string]
    $Rid = "win-x64"
)

#Requires -Version 7.0

Get-Command dotnet -ErrorAction Stop | Out-Null;

#TODO: Parse current platform RID from `dotnet --info` and default $Rid to that

#$ErrorActionPreference = 'Stop';

$projectDir = ".\src\$projectName";
$projectPath = "$projectDir\$projectName.csproj";
$artifacts = ".artifacts";

Write-Host "Cleaning up previous run";
if (Test-Path -Path "$artifacts\$projectName") {
    Get-ChildItem -Path "$artifacts\$projectName" | Remove-Item -Recurse -Force;
}
dotnet clean -v q --nologo
Get-ChildItem -Include bin -Recurse -Directory | Remove-Item -Recurse -Force;
Get-ChildItem -Include obj -Recurse -Directory | Remove-Item -Recurse -Force;

Write-Host "Publishing ${projectName}: dotnet publish -c Release -r $Rid --self-contained";
dotnet publish $projectPath -c Release -r $Rid --self-contained -v m --nologo -o "$artifacts\$projectName"
#dotnet publish $projectPath -c Release -r $Rid --self-contained -v m --nologo -o "$artifacts\$projectName" /p:PublishTrimmed=true /p:PublishSingleFile=true
#dotnet publish $projectPath -c Release -r $Rid --self-contained -v m --nologo -o "$artifacts\$projectName" /p:PublishTrimmed=true
#dotnet publish $projectPath -c Release -r $Rid --self-contained -v m --nologo -o "$artifacts\$projectName" /p:PublishTrimmed=true
#dotnet publish $projectPath -c Release -r $Rid --self-contained -v m --nologo -o "$artifacts\$projectName" /p:PublishAot=true /p:PublishSingleFile=false
Write-Host;

if ($LASTEXITCODE -ne 0 -or (Test-Path -Path "$artifacts\$projectName\$projectName.exe") -ne $true)
{
    Write-Error "Publish failed, see error above";
    return;
}

$appExe = Get-ChildItem -Path "$artifacts\$projectName\$projectName.exe";
$appSize = ($appExe.Length / (1024 * 1024)).ToString("#.##");
$appAccessTime = $appExe.LastAccessTime;
Write-Host "App executable size: $appSize MB";
Write-Host "App last access time: $appAccessTime";
Write-Host;

Write-Host "Running $projectName";
Write-Host;

Start-Process -FilePath ".\$artifacts\$projectName\$projectName.exe" -WorkingDirectory ".\$artifacts\$projectName" -NoNewWindow -Wait