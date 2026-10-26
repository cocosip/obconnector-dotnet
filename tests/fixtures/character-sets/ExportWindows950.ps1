param([Parameter(Mandatory = $true)][string]$Path)

# Optional regeneration input: 65536 little-endian Int32 scalar values; -1 means invalid.
$codec = [System.Text.CodePagesEncodingProvider]::Instance.GetEncoding(950,
    [System.Text.EncoderFallback]::ExceptionFallback, [System.Text.DecoderFallback]::ExceptionFallback)
$stream = [System.IO.File]::Create($Path)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    for ($code = 0; $code -lt 65536; $code++) {
        $scalar = -1
        $lead = $code -shr 8
        $trail = $code -band 255
        if ($code -lt 128) { $scalar = $code }
        elseif ($lead -ge 0x81 -and $lead -le 0xfe -and
            (($trail -ge 0x40 -and $trail -le 0x7e) -or ($trail -ge 0xa1 -and $trail -le 0xfe))) {
            try {
                $text = $codec.GetString([byte[]]@($lead, $trail))
                if ($text.Length -eq 1) { $scalar = [int]$text[0] }
            }
            catch [System.Management.Automation.MethodInvocationException] {
                if ($_.Exception.InnerException -isnot [System.Text.DecoderFallbackException]) { throw }
            }
        }
        $writer.Write([int]$scalar)
    }
}
finally { $writer.Dispose(); $stream.Dispose() }
