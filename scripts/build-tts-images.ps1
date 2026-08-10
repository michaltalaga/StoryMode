# Builds the container TTS images. Run once per engine (and again after editing docker/<name>/).
#
#   .\scripts\build-tts-images.ps1            # everything under docker/
#   .\scripts\build-tts-images.ps1 xtts       # just one
#
# Model weights are NOT baked into the images — they download on first render into models/<name>,
# which is a volume, so rebuilding an image re-downloads nothing and the weights stay inspectable
# on the host like every other artifact in this project.

param([string[]] $Engines)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

& docker version --format '{{.Server.Version}}' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker is not running. Start Docker Desktop and try again." }

$available = Get-ChildItem (Join-Path $repo 'docker') -Directory | Select-Object -ExpandProperty Name
$wanted = if ($Engines) { $Engines } else { $available }

foreach ($name in $wanted) {
    $context = Join-Path $repo "docker\$name"
    if (-not (Test-Path $context)) { throw "no docker/$name — available: $($available -join ', ')" }

    Write-Host "building storymode-$name:latest (several minutes the first time)..."
    & docker build -t "storymode-$name`:latest" $context
    if ($LASTEXITCODE -ne 0) { throw "docker build failed for $name" }

    New-Item -ItemType Directory -Force (Join-Path $repo "models\$name") | Out-Null
}

Write-Host "`ndone. The app starts a container itself on the first render that needs it."
Write-Host "Each engine stays unregistered until its licence is accepted in appsettings.json."
