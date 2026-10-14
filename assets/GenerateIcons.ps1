# Deterministic original artwork. No external fonts, images or network access.
# kea.svg is the single source of truth: geometry, the dark/light palettes, the monochrome
# roles and the small-size detail rules are read from it, so these PNG/ICO outputs, the SVG
# and the KeaMascot control (which reads the copied kea.svg at run time) cannot drift apart.
# Requires PowerShell 7 on Windows (System.Drawing/GDI+). Same input -> byte-identical output.
# A scratch preview (-OutputDirectory somewhere else) never touches the browser-helper toolbar
# icons: they are refreshed only for the real app assets or an explicit -ExtensionsDirectory.
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../src/Klippi.Desktop/Assets'),
    [string]$ExtensionsDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3
Add-Type -AssemblyName System.Drawing
$invariant = [Globalization.CultureInfo]::InvariantCulture
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (!$PSBoundParameters.ContainsKey('ExtensionsDirectory')) {
    $ExtensionsDirectory = if ($PSBoundParameters.ContainsKey('OutputDirectory')) { '' } else { Join-Path $PSScriptRoot '../extensions' }
}
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$sourcePath = Join-Path $PSScriptRoot 'kea.svg'
[xml]$svg = [IO.File]::ReadAllText($sourcePath)

function Get-Number([Xml.XmlElement]$node, [string]$name, [double]$default = 0) {
    $value = $node.GetAttribute($name)
    if ([string]::IsNullOrEmpty($value)) { return $default }
    return [double]::Parse($value, $invariant)
}

# Absolute M/L/H/V/C/Q/Z path data only; anything else is rejected so the C# side
# (Avalonia path mini-language) and this parser always agree on the shape.
function ConvertTo-GraphicsPath([string]$data) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new([Drawing.Drawing2D.FillMode]::Winding)
    $queue = [Collections.Generic.Queue[string]]::new([string[]]@([regex]::Matches($data, '[A-Za-z]|-?(?:\d+(?:\.\d*)?|\.\d+)') | ForEach-Object Value))
    $command = ''; $x = 0.0; $y = 0.0; $startX = 0.0; $startY = 0.0
    while ($queue.Count -gt 0) {
        if ($queue.Peek() -cmatch '^[A-Za-z]$') {
            $command = $queue.Dequeue()
            if ($command -ceq 'Z') { $path.CloseFigure(); $x = $startX; $y = $startY; continue }
        }
        $n = { [double]::Parse($queue.Dequeue(), $invariant) }
        switch -CaseSensitive ($command) {
            'M' { $x = & $n; $y = & $n; $startX = $x; $startY = $y; $path.StartFigure(); $command = 'L' }
            'L' { $nx = & $n; $ny = & $n; $path.AddLine([float]$x, [float]$y, [float]$nx, [float]$ny); $x = $nx; $y = $ny }
            'H' { $nx = & $n; $path.AddLine([float]$x, [float]$y, [float]$nx, [float]$y); $x = $nx }
            'V' { $ny = & $n; $path.AddLine([float]$x, [float]$y, [float]$x, [float]$ny); $y = $ny }
            'C' {
                $x1 = & $n; $y1 = & $n; $x2 = & $n; $y2 = & $n; $nx = & $n; $ny = & $n
                $path.AddBezier([float]$x, [float]$y, [float]$x1, [float]$y1, [float]$x2, [float]$y2, [float]$nx, [float]$ny); $x = $nx; $y = $ny
            }
            'Q' {
                $qx = & $n; $qy = & $n; $nx = & $n; $ny = & $n
                $path.AddBezier([float]$x, [float]$y, [float]($x + 2 / 3 * ($qx - $x)), [float]($y + 2 / 3 * ($qy - $y)), [float]($nx + 2 / 3 * ($qx - $nx)), [float]($ny + 2 / 3 * ($qy - $ny)), [float]$nx, [float]$ny); $x = $nx; $y = $ny
            }
            default { throw "Unsupported path command '$command' in kea.svg (use absolute M L H V C Q Z)." }
        }
    }
    return $path
}

function ConvertTo-Shape([Xml.XmlElement]$node) {
    switch ($node.LocalName) {
        'path' { return ConvertTo-GraphicsPath $node.GetAttribute('d') }
        'circle' {
            $path = [Drawing.Drawing2D.GraphicsPath]::new([Drawing.Drawing2D.FillMode]::Winding)
            $r = Get-Number $node 'r'
            $path.AddEllipse([float]((Get-Number $node 'cx') - $r), [float]((Get-Number $node 'cy') - $r), [float](2 * $r), [float](2 * $r))
            return $path
        }
        'rect' {
            $path = [Drawing.Drawing2D.GraphicsPath]::new([Drawing.Drawing2D.FillMode]::Winding)
            $left = Get-Number $node 'x'; $top = Get-Number $node 'y'; $w = Get-Number $node 'width'; $h = Get-Number $node 'height'; $d = 2 * (Get-Number $node 'rx')
            $path.AddArc([float]$left, [float]$top, [float]$d, [float]$d, 180, 90); $path.AddArc([float]($left + $w - $d), [float]$top, [float]$d, [float]$d, 270, 90)
            $path.AddArc([float]($left + $w - $d), [float]($top + $h - $d), [float]$d, [float]$d, 0, 90); $path.AddArc([float]$left, [float]($top + $h - $d), [float]$d, [float]$d, 90, 90)
            $path.CloseFigure()
            return $path
        }
        default { throw "Unsupported element '$($node.LocalName)' in kea.svg." }
    }
}

# Drawable elements in paint order. The structure is checked strictly so that a re-save from an
# SVG editor (layer groups, transforms, strokes, new element kinds) fails loudly here instead of
# silently producing icons with missing parts (KeaMascot.cs applies the same rules):
# - the first element is the tile <rect id="tile">; after it, top-level path/circle are drawn,
#   subject to their size rules;
# - display="none" on such a shape is only allowed for a small-size variant (data-below-size);
# - one <g data-pose data-origin display="none"> is a KeaMascot pose layer, never in the icons;
# - title/desc/metadata and editor-namespace elements are ignored; anything else is an error.
$svgNamespace = 'http://www.w3.org/2000/svg'
$drawable = @('path', 'circle')
$shapeAttributes = @('id', 'd', 'cx', 'cy', 'r', 'x', 'y', 'width', 'height', 'rx', 'fill', 'display')
function Assert-Shape([Xml.XmlElement]$node, [string]$where) {
    if ($node.NamespaceURI -ne $svgNamespace -or $node.LocalName -notin $drawable) { throw "Unsupported element <$($node.Name)> $where in kea.svg (only path and circle are drawn after the tile)." }
    foreach ($attribute in $node.Attributes) {
        if ($attribute.NamespaceURI -ne '' -or $attribute.LocalName.StartsWith('data-')) { continue }
        if ($attribute.LocalName -notin $shapeAttributes) { throw "Unsupported attribute '$($attribute.Name)' on #$($node.GetAttribute('id')) in kea.svg; the generator and KeaMascot would ignore it." }
    }
    if (!$node.HasAttribute('id')) { throw "Every shape in kea.svg needs an id (KeaMascot reads shapes by id)." }
    if (!$node.HasAttribute('fill')) { throw "Shape #$($node.GetAttribute('id')) in kea.svg has no fill." }
}
$elements = [Collections.Generic.List[Xml.XmlElement]]::new()
$poseLayers = 0
foreach ($node in $svg.DocumentElement.ChildNodes) {
    if ($node -isnot [Xml.XmlElement] -or $node.NamespaceURI -ne $svgNamespace -or $node.LocalName -in @('title', 'desc', 'metadata')) { continue }
    if ($node.LocalName -eq 'g') {
        if (!$node.HasAttribute('data-pose') -or !$node.HasAttribute('data-origin') -or $node.GetAttribute('display') -ne 'none') {
            throw "Group #$($node.GetAttribute('id')) in kea.svg is not a pose layer (<g data-pose=`"...`" data-origin=`"x y`" display=`"none`">). Ungroup editor layers before generating."
        }
        if (++$poseLayers -gt 1) { throw "kea.svg has more than one pose layer; KeaMascot supports one." }
        foreach ($child in $node.ChildNodes) { if ($child -is [Xml.XmlElement]) { Assert-Shape $child "inside pose group #$($node.GetAttribute('id'))" } }
        continue
    }
    if ($elements.Count -eq 0) {
        if ($node.LocalName -ne 'rect' -or $node.GetAttribute('id') -ne 'tile') { throw "kea.svg must start with the tile <rect id=`"tile`"> followed by the artwork." }
        $elements.Add($node)
        continue
    }
    Assert-Shape $node 'at the top level'
    if ($node.GetAttribute('display') -eq 'none' -and !$node.HasAttribute('data-below-size')) {
        throw "Shape #$($node.GetAttribute('id')) is hidden (display=`"none`") but is not a small-size variant (data-below-size)."
    }
    $elements.Add($node)
}
if ($elements.Count -lt 2) { throw "kea.svg must start with the tile <rect> followed by the artwork." }

# Small-size hinting: a detail is drawn when min-size <= pixel size < below-size.
function Test-Visible([Xml.XmlElement]$node, [int]$size) {
    return $size -ge (Get-Number $node 'data-min-size' 0) -and $size -lt (Get-Number $node 'data-below-size' 100000)
}

function New-KeaBitmap([int]$size, [string]$variant) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.ScaleTransform($size / 64.0, $size / 64.0)
        $mono = $variant -eq 'mono'
        # Monochrome is drawn as a coverage map (white = ink, black = cut-out) and then
        # converted to white-with-alpha, so cut-outs (eye, beak gap) stay anti-aliased.
        $graphics.Clear($(if ($mono) { [Drawing.Color]::Black } else { [Drawing.Color]::Transparent }))
        foreach ($node in $elements) {
            if (!(Test-Visible $node $size)) { continue }
            if ($mono) {
                $role = $node.GetAttribute('data-mono')
                if ($role -notin @('ink', 'hole')) { continue }
                $shape = ConvertTo-Shape $node
                try {
                    $gap = Get-Number $node 'data-mono-gap' 0
                    if ($gap -gt 0 -and $size -ge (Get-Number $node 'data-mono-gap-min-size' 0)) {
                        $pen = [Drawing.Pen]::new([Drawing.Color]::Black, [float]$gap); $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
                        try { $graphics.DrawPath($pen, $shape) } finally { $pen.Dispose() }
                    }
                    $brush = [Drawing.SolidBrush]::new($(if ($role -eq 'ink') { [Drawing.Color]::White } else { [Drawing.Color]::Black }))
                    try { $graphics.FillPath($brush, $shape) } finally { $brush.Dispose() }
                } finally { $shape.Dispose() }
                continue
            }
            $fill = if ($variant -eq 'light' -and $node.HasAttribute('data-light-fill')) { $node.GetAttribute('data-light-fill') } else { $node.GetAttribute('fill') }
            $shape = ConvertTo-Shape $node
            $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($fill))
            try { $graphics.FillPath($brush, $shape) } finally { $brush.Dispose(); $shape.Dispose() }
        }
    } finally { $graphics.Dispose() }
    if ($mono) { ConvertTo-WhiteAlpha $bitmap }
    return $bitmap
}

function ConvertTo-WhiteAlpha([Drawing.Bitmap]$bitmap) {
    $rect = [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height)
    $data = $bitmap.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadWrite, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $bytes = [byte[]]::new($data.Stride * $data.Height)
        [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        for ($i = 0; $i -lt $bytes.Length; $i += 4) { $alpha = $bytes[$i + 2]; $bytes[$i] = 255; $bytes[$i + 1] = 255; $bytes[$i + 2] = 255; $bytes[$i + 3] = $alpha }
        [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
    } finally { $bitmap.UnlockBits($data) }
}

function Save-Kea([int]$size, [string]$variant, [string]$name) {
    $bitmap = New-KeaBitmap $size $variant
    try { $bitmap.Save((Join-Path $resolvedOutput $name), [Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}

# Every variant at every icon size (20 px is the small icon at 125%, 48 px the large one at 150%).
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
foreach ($iconSize in $sizes) {
    Save-Kea $iconSize 'dark' "kea-$iconSize.png"
    Save-Kea $iconSize 'light' "kea-light-$iconSize.png"
    Save-Kea $iconSize 'mono' "kea-mono-$iconSize.png"
}
# ICO frames contain the deterministic PNGs; Windows supports PNG-backed ICO frames.
$stream = [IO.File]::Create((Join-Path $resolvedOutput 'kea.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    $frames = @()
    foreach ($iconSize in $sizes) {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $resolvedOutput "kea-$iconSize.png")); $frames += , $bytes
        $dimension = if ($iconSize -eq 256) { 0 } else { $iconSize }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]$offset)
        $offset += $bytes.Length
    }
    foreach ($bytes in $frames) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose(); $stream.Dispose() }
Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $resolvedOutput 'kea.svg')

# Browser helpers: every "icons/kea-<size>.png" a manifest references is refreshed from the
# app icon of that size (dark tile, readable on light and dark toolbars), so the toolbar icon
# can never drift from the app. Manifests that reference no icons are left alone.
if (![string]::IsNullOrEmpty($ExtensionsDirectory) -and (Test-Path -LiteralPath $ExtensionsDirectory)) {
    foreach ($manifest in Get-ChildItem -LiteralPath $ExtensionsDirectory -Filter 'manifest.json' -Recurse -Depth 1 -File | Sort-Object FullName) {
        foreach ($match in [regex]::Matches([IO.File]::ReadAllText($manifest.FullName), '"(icons/kea-(\d+)\.png)"')) {
            $iconSize = [int]$match.Groups[2].Value
            if ($iconSize -notin $sizes) { throw "$($manifest.FullName) references $($match.Groups[1].Value); available sizes are $($sizes -join ', ')." }
            $target = Join-Path $manifest.DirectoryName $match.Groups[1].Value
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            Copy-Item -LiteralPath (Join-Path $resolvedOutput "kea-$iconSize.png") -Destination $target -Force
        }
    }
}
