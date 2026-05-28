param(
    [Parameter(Mandatory=$true)][string]$InputPath,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
Copy-Item -LiteralPath $InputPath -Destination $OutputPath -Force
exit 0
