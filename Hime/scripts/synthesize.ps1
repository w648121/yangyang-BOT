param(
    [Parameter(Mandatory = $true)]
    [string]$Text,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$Voice = "Microsoft Huihui Desktop",

    [ValidateRange(-10, 10)]
    [int]$Rate = 1,

    [ValidateRange(0, 100)]
    [int]$Volume = 100
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Speech

$directory = Split-Path -Parent $OutputPath
if ($directory) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$synthesizer = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
    $installedNames = @($synthesizer.GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name })
    if ($installedNames -contains $Voice) {
        $synthesizer.SelectVoice($Voice)
    }

    $synthesizer.Rate = $Rate
    $synthesizer.Volume = $Volume
    $synthesizer.SetOutputToWaveFile($OutputPath)
    $synthesizer.Speak($Text)
}
finally {
    $synthesizer.Dispose()
}

if (-not (Test-Path -LiteralPath $OutputPath) -or (Get-Item -LiteralPath $OutputPath).Length -le 44) {
    throw "TTS did not produce a valid WAV file: $OutputPath"
}
