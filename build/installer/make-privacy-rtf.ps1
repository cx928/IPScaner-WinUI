<#
.SYNOPSIS
    Converts the plain-text privacy policy into the RTF that WiX's licence page
    requires.

.DESCRIPTION
    WiX's WixUILicenseRtf must be real RTF. The policy is Chinese, and the Richedit
    control that renders it is ANSI-based, so every non-ASCII character is emitted
    as an RTF \uN? escape rather than raw UTF-8 bytes — otherwise the agreement
    page shows mojibake regardless of the file's encoding.

    Run by build\package.ps1; can also be run on its own after editing the .txt.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Destination
)

$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
if (-not $Source) { $Source = Join-Path $here "privacy-zh.txt" }
if (-not $Destination) { $Destination = Join-Path $here "privacy-zh.rtf" }

if (-not (Test-Path $Source)) { throw "privacy text not found: $Source" }

$text = Get-Content $Source -Raw -Encoding UTF8

# Escape the RTF control characters first.
$escaped = $text.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}')

$sb = [System.Text.StringBuilder]::new()
foreach ($ch in $escaped.ToCharArray()) {
    $code = [int]$ch
    if ($ch -eq "`r") { continue }
    if ($ch -eq "`n") { [void]$sb.Append("\par`r`n") }
    elseif ($code -lt 128) { [void]$sb.Append($ch) }
    else {
        # \uc1 declares a one-character fallback, so \uN? is well formed.
        #
        # Build the escape by concatenation, NOT with "\u$code?": PowerShell allows
        # '?' inside a variable name, so "$code?" parses as the (undefined) variable
        # `code?` and every escape silently loses its digits, producing a file full
        # of bare "\u" that WordPad cannot render.
        [void]$sb.Append("\u" + $code + "?")
    }
}

$header = '{\rtf1\ansi\ansicpg936\deff0\uc1' + "`r`n" +
          '{\fonttbl{\f0\fnil\fcharset134 Microsoft YaHei;}{\f1\fnil\fcharset134 SimSun;}}' + "`r`n" +
          '\viewkind4\pard\f0\fs18 '
$footer = "`r`n}"

$rtf = $header + $sb.ToString() + $footer

# ASCII only at this point, so plain ASCII encoding is correct and safest.
[System.IO.File]::WriteAllText($Destination, $rtf, [System.Text.Encoding]::ASCII)

$info = Get-Item $Destination
Write-Host ("privacy RTF written: {0} ({1} bytes)" -f $info.FullName, $info.Length)
