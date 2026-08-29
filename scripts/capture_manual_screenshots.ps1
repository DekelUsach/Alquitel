<#
.SYNOPSIS
    Captura las pantallas de Alquitel para el Manual de Usuario (docs/MANUAL_DE_USUARIO.md).

.DESCRIPTION
    Lanza Alquitel.UI.exe, recorre cada sección con sus atajos de teclado
    (Ctrl+1 .. Ctrl+9, Ctrl+K, Ctrl+T) y guarda un PNG de la ventana activa
    en docs/manual_images/.

    Requisitos:
      - Windows con sesión de escritorio interactiva (no funciona por SSH/servicio).
      - La app compilada (dotnet build) o instalada.
      - Sesión ya iniciada en la app (session.json vigente) para saltear el login;
        si aparece LoginWindow, el script la captura y espera a que el operador
        entre a mano (parámetro -WaitLoginSeconds).

.PARAMETER ExePath
    Ruta al ejecutable. Por defecto busca el build de Debug y, si no está, el instalado
    en %LocalAppData%\Alquitel\current.

.PARAMETER OutputDir
    Carpeta destino de las imágenes. Por defecto docs/manual_images.

.PARAMETER WaitLoginSeconds
    Segundos de espera para que una persona complete el login manualmente.

.PARAMETER KeepOpen
    No cierra la aplicación al terminar.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\capture_manual_screenshots.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\capture_manual_screenshots.ps1 -WaitLoginSeconds 90 -KeepOpen
#>
[CmdletBinding()]
param(
    [string] $ExePath,
    [string] $OutputDir,
    [int]    $WaitLoginSeconds = 60,
    [switch] $KeepOpen,
    [switch] $OnlyLogin
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# ── Interop: foco de ventana + rectángulo real (DWM incluye la sombra, se recorta) ──
if (-not ('Native.Win' -as [type])) {
    Add-Type -Namespace Native -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
}

function Resolve-Exe {
    param([string] $Explicit)
    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "No existe el ejecutable indicado: $Explicit" }
        return (Resolve-Path $Explicit).Path
    }
    $repo = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        Join-Path $repo 'Alquitel.UI\bin\Debug\net8.0-windows\Alquitel.UI.exe'
        Join-Path $repo 'Alquitel.UI\bin\Release\net8.0-windows\Alquitel.UI.exe'
        Join-Path $env:LOCALAPPDATA 'Alquitel\current\Alquitel.UI.exe'
        Join-Path $env:LOCALAPPDATA 'Alquitel\Alquitel.UI.exe'
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return (Resolve-Path $c).Path } }
    throw "No se encontró Alquitel.UI.exe. Compilá con 'dotnet build' o pasá -ExePath."
}

function Get-WindowBounds {
    param([IntPtr] $Handle)
    $r = New-Object Native.Win+RECT
    # DWMWA_EXTENDED_FRAME_BOUNDS = 9 → sin el borde invisible de la sombra
    $ok = [Native.Win]::DwmGetWindowAttribute($Handle, 9, [ref] $r, 16)
    if ($ok -ne 0) { [void][Native.Win]::GetWindowRect($Handle, [ref] $r) }
    [PSCustomObject]@{
        X      = $r.Left
        Y      = $r.Top
        Width  = $r.Right  - $r.Left
        Height = $r.Bottom - $r.Top
    }
}

# CopyFromScreen fotografía píxeles del escritorio: si la ventana de Alquitel no está
# realmente al frente, la captura termina mostrando otra aplicación. Se verifica.
function Ensure-Foreground {
    param([IntPtr] $Handle, [int] $Attempts = 12)
    for ($i = 0; $i -lt $Attempts; $i++) {
        if ([Native.Win]::GetForegroundWindow() -eq $Handle) { return $true }
        # Windows solo cede el foco al proceso que "tiene" la entrada: un ALT suelto
        # levanta ese bloqueo antes de pedir SetForegroundWindow.
        [System.Windows.Forms.SendKeys]::SendWait('%')
        [void][Native.Win]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 350
    }
    return ([Native.Win]::GetForegroundWindow() -eq $Handle)
}

function Save-WindowShot {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [IntPtr] $Handle = [IntPtr]::Zero,
        [int] $SettleMs = 900
    )
    if ($Handle -eq [IntPtr]::Zero) { $Handle = [Native.Win]::GetForegroundWindow() }
    Start-Sleep -Milliseconds $SettleMs   # deja terminar animaciones y carga de datos

    $b = Get-WindowBounds -Handle $Handle
    if ($b.Width -le 0 -or $b.Height -le 0) {
        Write-Warning "Ventana sin tamaño válido para '$Name' — se omite."
        return
    }

    # PrintWindow con PW_RENDERFULLCONTENT (2) le pide a la propia ventana que se
    # dibuje en un bitmap. No fotografía el escritorio: aunque otra aplicación tape
    # a Alquitel, la captura sale limpia y nunca se filtra la pantalla del usuario.
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $ok = $false
        try {
            $hdc = $g.GetHdc()
            try { $ok = [Native.Win]::PrintWindow($Handle, $hdc, 2) }
            finally { $g.ReleaseHdc($hdc) }
        } finally { $g.Dispose() }

        if (-not $ok) {
            Write-Warning "PrintWindow falló para '$Name' — se omite."
            return
        }
        $path = Join-Path $script:OutDir "$Name.png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host ("  [OK] {0}.png  ({1}x{2})" -f $Name, $b.Width, $b.Height)
    } finally { $bmp.Dispose() }
}

function Send-Keys {
    param([string] $Keys, [int] $PauseMs = 700)
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds $PauseMs
}

# Velopack puede mostrar primero su ventana de actualización e incluso relanzar el
# proceso: nunca hay que cachear un MainWindowHandle. Se resuelve por título, sobre
# todos los procesos Alquitel.UI vivos, cada vez que hace falta.
function Find-Window {
    param([string] $TitleLike)
    foreach ($p in @(Get-Process -Name 'Alquitel.UI' -ErrorAction SilentlyContinue)) {
        try { $p.Refresh() } catch { continue }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero -and $p.MainWindowTitle -like $TitleLike) {
            return [PSCustomObject]@{ Handle = $p.MainWindowHandle; Title = $p.MainWindowTitle; Process = $p }
        }
    }
    return $null
}

function Wait-Window {
    param([string] $TitleLike, [int] $TimeoutSeconds = 60)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $w = Find-Window -TitleLike $TitleLike
        if ($w) { return $w }
        Start-Sleep -Milliseconds 700
    }
    return $null
}

# ── UI Automation: para los controles sin atajo de teclado ──
# En WPF el ToolTip viaja a UIA como HelpText y el Content textual como Name, así que
# se puede invocar un botón sin depender de coordenadas (que rompen con el escalado DPI).
function Invoke-UiaButton {
    param(
        [Parameter(Mandatory)] [IntPtr] $WindowHandle,
        [string] $NameLike,
        [string] $HelpLike,
        [string] $ContainsText
    )
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
        if (-not $root) { return $false }
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
        foreach ($el in $all) {
            $name = $el.Current.Name
            $help = $el.Current.HelpText
            $hit = ($NameLike -and $name -like $NameLike) -or ($HelpLike -and $help -like $HelpLike)

            # Los botones cuyo contenido es un StackPanel (ícono + etiqueta) no exponen
            # Name: hay que mirar los TextBlock de adentro.
            if (-not $hit -and $ContainsText) {
                $textCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Text)
                foreach ($t in $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
                    if ($t.Current.Name -like $ContainsText) { $hit = $true; break }
                }
            }

            if ($hit) {
                $pattern = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $pattern.Invoke()
                return $true
            }
        }
    } catch {
        Write-Warning "UI Automation falló: $($_.Exception.Message)"
    }
    return $false
}

function Focus-Shell {
    param([switch] $Maximize)
    $w = Find-Window -TitleLike '*Sistema Administrativo*'
    if (-not $w) { return $null }
    if ($Maximize) { [void][Native.Win]::ShowWindow($w.Handle, 3) }   # SW_MAXIMIZE
    [void](Ensure-Foreground -Handle $w.Handle)
    Start-Sleep -Milliseconds 400
    return $w.Handle
}

# ─────────────────────────── preparación ───────────────────────────
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'docs\manual_images' }
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$script:OutDir = (Resolve-Path $OutputDir).Path

$exe = Resolve-Exe -Explicit $ExePath
Write-Host "Ejecutable : $exe"
Write-Host "Destino    : $script:OutDir"
Write-Host ''

$proc = Start-Process -FilePath $exe -PassThru
Write-Host "Aplicación iniciada (PID $($proc.Id)). Esperando la ventana…"

# Ventana de login: solo aparece si no hay sesión guardada vigente.
$login = Wait-Window -TitleLike '*Iniciar sesi*' -TimeoutSeconds 12
if ($login) {
    [void][Native.Win]::SetForegroundWindow($login.Handle)
    Save-WindowShot -Name '01_login' -Handle $login.Handle -SettleMs 1800
    Write-Host "Login abierto. Completá el ingreso a mano (hasta $WaitLoginSeconds s)…"
}

if ($OnlyLogin) {
    if (-not $login) {
        Write-Warning 'No apareció el login (hay sesión guardada vigente). Usá -OnlyLogin con la sesión cerrada.'
    }
    foreach ($p in @(Get-Process -Name 'Alquitel.UI' -ErrorAction SilentlyContinue)) {
        try { $p.CloseMainWindow() | Out-Null } catch {}
    }
    Start-Sleep -Seconds 2
    foreach ($p in @(Get-Process -Name 'Alquitel.UI' -ErrorAction SilentlyContinue)) {
        try { if (-not $p.HasExited) { $p.Kill() } } catch {}
    }
    return
}

$shell = Wait-Window -TitleLike '*Sistema Administrativo*' `
                     -TimeoutSeconds ($(if ($login) { $WaitLoginSeconds } else { 90 }))
if (-not $shell) { throw 'No apareció la ventana principal (¿login sin completar?).' }

$hwnd = Focus-Shell -Maximize
if (-not $hwnd) { throw 'No se pudo enfocar la ventana principal.' }
Start-Sleep -Seconds 3   # carga inicial del Dashboard

# ─────────────────────── recorrido de secciones ───────────────────────
# Cada entrada: nombre de archivo, atajo de teclado, espera extra por carga de datos.
$sections = @(
    @{ Name = '02_pantalla_principal';  Keys = '^1'; Settle = 2200 }  # Dashboard
    @{ Name = '03_nuevo_presupuesto';   Keys = '^2'; Settle = 2500 }  # Armar pedido
    @{ Name = '05_productos';           Keys = '^3'; Settle = 2200 }  # Catálogo
    @{ Name = '06_clientes';            Keys = '^4'; Settle = 2200 }  # Directorio
    @{ Name = '07_ubicaciones';         Keys = '^5'; Settle = 2200 }  # Padrón de lugares
    @{ Name = '08_presupuestos';        Keys = '^6'; Settle = 2500 }  # Explorador de .docx
    @{ Name = '09_seguimiento';         Keys = '^9'; Settle = 2500 }  # Pool de órdenes
    @{ Name = '10_reportes';            Keys = '^8'; Settle = 3000 }  # Gráficos (solo Admin)
    @{ Name = '11_configuracion';       Keys = '^7'; Settle = 2200 }  # Ajustes (solo Admin)
)

foreach ($s in $sections) {
    Write-Host "Capturando $($s.Name)…"
    $hwnd = Focus-Shell
    if (-not $hwnd) { Write-Warning "Ventana principal no disponible — se omite $($s.Name)."; continue }
    Send-Keys -Keys $s.Keys -PauseMs 400
    Save-WindowShot -Name $s.Name -Handle $hwnd -SettleMs $s.Settle
}

# ── Pantallas que no tienen atajo: se abren por UI Automation ──

# 04 · Asistente de IA (pedido automático) dentro del armador
Write-Host 'Capturando 04_asistente_ia…'
$hwnd = Focus-Shell
if ($hwnd) {
    Send-Keys -Keys '^2' -PauseMs 1600
    if (Invoke-UiaButton -WindowHandle $hwnd -HelpLike 'Pedido autom*') {
        Save-WindowShot -Name '04_asistente_ia' -Handle $hwnd -SettleMs 1400
        # Se vuelve a plegar el panel para no dejar el armador en un estado raro
        [void](Invoke-UiaButton -WindowHandle $hwnd -HelpLike 'Pedido autom*')
    } else {
        Write-Warning 'No se encontró el botón del pedido automático (varita).'
    }
}

# 09b · Órdenes de trabajo (no tiene atajo: se entra por el botón del menú lateral)
Write-Host 'Capturando 09b_ordenes_trabajo…'
$hwnd = Focus-Shell
if ($hwnd) {
    if (Invoke-UiaButton -WindowHandle $hwnd -NameLike '*rdenes de Trabajo*' -ContainsText '*rdenes de Trabajo*') {
        Save-WindowShot -Name '09b_ordenes_trabajo' -Handle $hwnd -SettleMs 2200
    } else {
        Write-Warning 'No se encontró el botón «Órdenes de Trabajo» (¿rol sin permiso?).'
    }
}

# 05b · Taller de producto (ficha de edición del catálogo)
Write-Host 'Capturando 05b_taller_producto…'
$hwnd = Focus-Shell
if ($hwnd) {
    Send-Keys -Keys '^3' -PauseMs 1600
    if (Invoke-UiaButton -WindowHandle $hwnd -NameLike '*Nuevo producto*' -ContainsText 'Nuevo producto') {
        Save-WindowShot -Name '05b_taller_producto' -Handle $hwnd -SettleMs 1600
        [void](Invoke-UiaButton -WindowHandle $hwnd -HelpLike 'Volver al cat*')
        Start-Sleep -Milliseconds 800
    } else {
        Write-Warning 'No se encontró el botón «Nuevo producto».'
    }
}

# 06b · Ficha lateral de cliente
Write-Host 'Capturando 06b_ficha_cliente…'
$hwnd = Focus-Shell
if ($hwnd) {
    Send-Keys -Keys '^4' -PauseMs 1600
    if (Invoke-UiaButton -WindowHandle $hwnd -NameLike '*Nuevo cliente*' -ContainsText 'Nuevo cliente') {
        Save-WindowShot -Name '06b_ficha_cliente' -Handle $hwnd -SettleMs 1400
        [void](Invoke-UiaButton -WindowHandle $hwnd -HelpLike 'Cerrar ficha sin guardar')
        Start-Sleep -Milliseconds 600
    } else {
        Write-Warning 'No se encontró el botón «Nuevo cliente».'
    }
}

# Paleta de comandos (Ctrl+K) — ventana propia, se captura la que quede al frente.
Write-Host 'Capturando 12_paleta_comandos…'
if (Focus-Shell) {
    Send-Keys -Keys '^k' -PauseMs 1400
    Save-WindowShot -Name '12_paleta_comandos' -SettleMs 600
    Send-Keys -Keys '{ESC}' -PauseMs 600
}

# Tema opuesto al que tenga la app en ese momento (Ctrl+T), y vuelta al original.
Write-Host 'Capturando 13_modo_oscuro…'
$hwnd = Focus-Shell
if ($hwnd) {
    Send-Keys -Keys '^1' -PauseMs 1400
    Send-Keys -Keys '^t' -PauseMs 1400
    Save-WindowShot -Name '13_modo_oscuro' -Handle $hwnd -SettleMs 1200
    Send-Keys -Keys '^t' -PauseMs 900     # restaura el tema original
}

Write-Host ''
Write-Host 'Capturas terminadas.'
Get-ChildItem $script:OutDir -Filter *.png | Sort-Object Name |
    ForEach-Object { Write-Host ("  {0,-34} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB)) }

if (-not $KeepOpen) {
    Write-Host ''
    Write-Host 'Cerrando la aplicación…'
    foreach ($p in @(Get-Process -Name 'Alquitel.UI' -ErrorAction SilentlyContinue)) {
        try { $p.CloseMainWindow() | Out-Null } catch {}
    }
    Start-Sleep -Seconds 2
    foreach ($p in @(Get-Process -Name 'Alquitel.UI' -ErrorAction SilentlyContinue)) {
        try { if (-not $p.HasExited) { $p.Kill() } } catch {}
    }
}
