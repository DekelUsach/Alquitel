param(
    [string]$SourcePng = "C:\Proyects\alqui\Alquitel\alquitel-logo.png",
    [string]$OutIco = "C:\Proyects\alqui\Alquitel\Alquitel.UI\Assets\app.ico",
    [string]$OutPng256 = "C:\Proyects\alqui\Alquitel\Alquitel.UI\Assets\logo-mark.png"
)

Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile($SourcePng)

# Canvas cuadrado centrado (transparente) del lado mas largo del logo original,
# para no recortar a ciegas la cinta/wordmark sin verificar pixeles.
$side = [Math]::Max($src.Width, $src.Height)
$square = New-Object System.Drawing.Bitmap $side, $side
$g = [System.Drawing.Graphics]::FromImage($square)
$g.Clear([System.Drawing.Color]::Transparent)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$offsetX = [int](($side - $src.Width) / 2)
$offsetY = [int](($side - $src.Height) / 2)
$g.DrawImage($src, $offsetX, $offsetY, $src.Width, $src.Height)
$g.Dispose()

function Resize-Square([System.Drawing.Bitmap]$bmp, [int]$size) {
    $out = New-Object System.Drawing.Bitmap $size, $size
    $gr = [System.Drawing.Graphics]::FromImage($out)
    $gr.Clear([System.Drawing.Color]::Transparent)
    $gr.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gr.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $gr.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gr.DrawImage($bmp, 0, 0, $size, $size)
    $gr.Dispose()
    return $out
}

$sizes = @(16,24,32,48,64,128,256)
$pngBytesList = New-Object System.Collections.Generic.List[byte[]]
foreach ($s in $sizes) {
    $resized = Resize-Square $square $s
    $ms = New-Object System.IO.MemoryStream
    $resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytesList.Add($ms.ToArray())
    $ms.Dispose()
    if ($s -eq 256) { $resized.Save($OutPng256, [System.Drawing.Imaging.ImageFormat]::Png) }
    $resized.Dispose()
}

# Construir .ico multi-resolucion (frames PNG embebidos, formato soportado
# desde Windows Vista en adelante para iconos >= 16px).
$fs = New-Object System.IO.FileStream $OutIco, ([System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter $fs

$count = $sizes.Count
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type = icon
$bw.Write([UInt16]$count)

$headerSize = 6
$dirEntrySize = 16
$offset = $headerSize + ($dirEntrySize * $count)

for ($i = 0; $i -lt $count; $i++) {
    $s = $sizes[$i]
    $bytes = $pngBytesList[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b)          # width (0 = 256)
    $bw.Write([byte]$b)          # height
    $bw.Write([byte]0)           # color palette
    $bw.Write([byte]0)           # reserved
    $bw.Write([UInt16]1)         # color planes
    $bw.Write([UInt16]32)        # bits per pixel
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
for ($i = 0; $i -lt $count; $i++) {
    $bw.Write($pngBytesList[$i])
}
$bw.Flush()
$bw.Close()
$fs.Close()
$square.Dispose()
$src.Dispose()

Write-Output "OK: $OutIco ($count frames), $OutPng256"
