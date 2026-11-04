<#
.SYNOPSIS
  无人工回归：验证「AI 在商店买『删牌』」只作用在 AI 自己身上（不删真人的牌、不扣真人的钱、不弹界面）。

.DESCRIPTION
  流程：启动游戏（必要时）→ 回到主菜单根界面 → 开多人房 → 选角色 + 添加 AI 队友 → 开局 →
  调用 mod 的测试动作 sts2_test_shop_removal，由 mod 在游戏内替 AI 真正走一遍删牌，
  把「AI 金币/牌组变化、真人金币/牌组不变、没有新 overlay」的断言结果回传。
  跑完默认放弃这局测试存档并关闭游戏。

  前置：AiTeammate 设置里「外部 Agent（dsh）」已开启（默认 26333 端口）。
  该动作会消耗 AI 的商店随机数并真的改对局数据，请只当一次性测试局用。

.PARAMETER Launcher
  steam = 用 steam://rungameid/2868840 启动（默认：Steamworks 正常初始化，不会有「Steam 未运行」弹窗；游戏用 sts2_quit_app 自己退出）
  exe   = 直接起 SlayTheSpire2.exe（不经过 Steam；实测会弹「Steam 未初始化」模态框，需要手动点掉）
  bat   = 用游戏目录里的 debug-测试.bat 启动（带 -clientId=72111，等同直接起 exe）

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\test-shop-removal.ps1
  powershell -ExecutionPolicy Bypass -File tools\test-shop-removal.ps1 -ReuseGame -KeepGame
  powershell -ExecutionPolicy Bypass -File tools\test-shop-removal.ps1 -Launcher bat -Character SILENT
#>
param(
    [int]$Port = 26333,
    [string]$Character = "IRONCLAD",
    [ValidateSet('steam', 'exe', 'bat')]
    [string]$Launcher = 'steam',
    [switch]$ReuseGame,
    [switch]$KeepGame,
    [int]$TimeoutSeconds = 180,
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'
$script:ExitCode = 1
$script:GameDir = 'F:\steam\steamapps\common\Slay the Spire 2'
$script:GameExe = Join-Path $script:GameDir 'SlayTheSpire2.exe'
$script:DebugBat = Join-Path $script:GameDir 'debug-测试.bat'
$script:SteamAppId = '2868840'
$script:ReportPath = Join-Path $env:TEMP 'sts2-shop-removal-report.json'

function Write-Step([string]$Message) { Write-Host "[test] $Message" -ForegroundColor Cyan }

function Invoke-Bridge {
    param([string]$Call, [hashtable]$Arguments = @{})
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $Port)
        $stream = $client.GetStream()
        $encoding = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($stream, $encoding)
        $reader = New-Object System.IO.StreamReader($stream, $encoding)
        $writer.AutoFlush = $true
        $payload = @{
            id    = [guid]::NewGuid().ToString('N')
            token = ''
            call  = $Call
            args  = $Arguments
        } | ConvertTo-Json -Compress -Depth 10
        $writer.WriteLine($payload)
        $line = $reader.ReadLine()
        if (-not $line) { throw "bridge returned nothing for $Call" }
        return ($line | ConvertFrom-Json)
    }
    finally {
        if ($client) { $client.Close() }
    }
}

function Get-BridgeText([object]$Response) {
    if (-not $Response.ok) { throw "bridge call failed: $($Response.error)" }
    return [string]$Response.result.text
}

function Get-GameState { (Get-BridgeText (Invoke-Bridge 'sts2_state')) | ConvertFrom-Json }

function Wait-Until {
    param([scriptblock]$Condition, [int]$Seconds = 60, [string]$What = 'condition')
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $Condition) { return $true } } catch { }
        Start-Sleep -Milliseconds 700
    }
    throw "timeout waiting for $What"
}

function Test-Bridge {
    try { return [bool](Invoke-Bridge 'sts2_hello').ok } catch { return $false }
}

function Test-InLobby([object]$State) {
    return ($State.phase -in @('menu', 'char_select', 'daily_lobby', 'custom_lobby'))
}

function Start-GameProcess {
    Write-Step "启动游戏（launcher=$Launcher）..."
    switch ($Launcher) {
        'steam' { Start-Process "steam://rungameid/$($script:SteamAppId)"; return }
        'bat' {
            if (-not (Test-Path $script:DebugBat)) { throw "找不到 $($script:DebugBat)" }
            if ($ExtraArgs.Count -gt 0) { Start-Process -FilePath $script:DebugBat -ArgumentList $ExtraArgs }
            else { Start-Process -FilePath $script:DebugBat }
            return
        }
        default {
            if (-not (Test-Path $script:GameExe)) { Start-Process "steam://rungameid/$($script:SteamAppId)"; return }
            if ($ExtraArgs.Count -gt 0) { Start-Process -FilePath $script:GameExe -ArgumentList $ExtraArgs }
            else { Start-Process -FilePath $script:GameExe }
        }
    }
}

# ---- 1) 连上游戏（必要时启动） -------------------------------------------------
if (-not (Test-Bridge)) {
    if ($ReuseGame) { throw "游戏桥接不可用（127.0.0.1:$Port）。先开游戏并确认「外部 Agent」已启用。" }
    Start-GameProcess
    [void](Wait-Until -Seconds $TimeoutSeconds -What '桥接就绪' -Condition { Test-Bridge })
}
Write-Step "已连接 127.0.0.1:$Port"

# ---- 2) 回到主菜单根界面 ------------------------------------------------------
$deadline = (Get-Date).AddSeconds(90)
$atRoot = $false
while ((Get-Date) -lt $deadline) {
    $state = Get-GameState
    if ($state.blocked_by -eq 'modal') {
        throw '游戏里有个 NGenericPopup 模态框挡住了菜单（bridge 不能点模态按钮），请手动关掉后重跑。'
    }
    if (Test-InLobby $state) {
        if ($state.menu.screen -eq 'main') { $atRoot = $true; break }
        try { Invoke-Bridge 'sts2_proceed' | Out-Null } catch { }
    } else {
        Write-Step "当前在对局内（phase=$($state.phase)），先退出到主菜单"
        try { Invoke-Bridge 'sts2_quit_to_menu' | Out-Null } catch { }
    }
    Start-Sleep -Milliseconds 900
}
if (-not $atRoot) { throw '回不到主菜单根界面' }

# ---- 3) 开多人房 + 选角色 + 添加 AI + 开局 -------------------------------------
Write-Step '创建多人房'
Invoke-Bridge 'sts2_host' @{ mode = 'standard' } | Out-Null
[void](Wait-Until -Seconds 45 -What '选人界面' -Condition { (Get-GameState).phase -eq 'char_select' })

$st = Get-GameState
$charOption = $st.options | Where-Object { $_.kind -eq 'character' -and $_.id -eq $Character } | Select-Object -First 1
if (-not $charOption) { throw "选人界面没有角色 $Character" }
Invoke-Bridge 'sts2_choose' @{ index = [int]$charOption.index } | Out-Null
Start-Sleep -Milliseconds 500

$st = Get-GameState
$aiJoin = $st.options | Where-Object { $_.kind -eq 'ai_join' } | Select-Object -First 1
if ($aiJoin) {
    Write-Step "添加 AI 队友（index=$($aiJoin.index)）"
    Invoke-Bridge 'sts2_choose' @{ index = [int]$aiJoin.index } | Out-Null
}

# AI 入房后选项卡里会出现「移除 AI」；snapshot 的 ai_in_lobby 字段刷新较慢，用它做唯一判据。
[void](Wait-Until -Seconds 25 -What 'AI 进房' -Condition {
    $s = Get-GameState
    ($s.menu.lobby.ai_in_lobby -eq $true) -or
    (@($s.options | Where-Object { $_.id -eq 'remove_ai' }).Count -gt 0) -or
    (@($s.menu.lobby.players | Where-Object { $_.is_ai }).Count -gt 0)
})

# 开始：房间界面的确认/取消准备会因为「本地玩家是否已准备」互换，按状态重试。
$started = $false
for ($attempt = 0; $attempt -lt 6 -and -not $started; $attempt++) {
    $st = Get-GameState
    if (-not (Test-InLobby $st)) { $started = $true; break }
    if ($st.phase -eq 'menu') { throw '被弹回主菜单了（开局失败）' }
    $confirm = $st.options | Where-Object { $_.id -eq 'confirm' } | Select-Object -First 1
    if (-not $confirm) {
        $unready = $st.options | Where-Object { $_.id -eq 'unready' } | Select-Object -First 1
        if ($unready) {
            Invoke-Bridge 'sts2_choose' @{ index = [int]$unready.index } | Out-Null
            Start-Sleep -Milliseconds 800
            continue
        }
        throw '找不到开始按钮'
    }
    Write-Step '开始对局'
    Invoke-Bridge 'sts2_choose' @{ index = [int]$confirm.index } | Out-Null
    Start-Sleep -Seconds 2
    if (-not (Test-InLobby (Get-GameState))) { $started = $true }
}
if (-not $started) { throw '对局没有开始' }

# ---- 4) 等 AI 入局后跑自检 ----------------------------------------------------
Write-Step '等待 AI 入局并执行商店删牌自检...'
$report = $null
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    $resp = Invoke-Bridge 'sts2_test_shop_removal' @{}
    if ($resp.ok) { $report = (Get-BridgeText $resp) | ConvertFrom-Json; break }
    Start-Sleep -Seconds 1
}
if (-not $report) { throw '自检动作一直失败（AI 玩家可能还没入局）' }
$report | ConvertTo-Json -Depth 10 | Set-Content -Path $script:ReportPath -Encoding UTF8

# ---- 5) 输出结果 --------------------------------------------------------------
Write-Host ''
Write-Host '========== 商店删牌自检 ==========' -ForegroundColor Yellow
Write-Host ("购买成功     : {0}（价格 {1}）" -f $report.purchased, $report.cost)
Write-Host ("AI 金币      : {0} -> {1}" -f $report.ai_gold_before, $report.ai_gold_after)
Write-Host ("AI 牌组      : {0} -> {1}，删除 [{2}]" -f $report.ai_deck_before, $report.ai_deck_after, (($report.removed_cards -join ',')))
Write-Host ("删牌计数     : {0}，删牌项已用={1}" -f $report.removals_used, $report.entry_used)
if (-not $report.human_is_ai) {
    Write-Host ("真人金币     : {0} -> {1}" -f $report.human_gold_before, $report.human_gold_after)
    Write-Host ("真人牌组     : {0} -> {1}" -f $report.human_deck_before, $report.human_deck_after)
    Write-Host ("真人界面     : overlay {0} -> {1}" -f $report.overlays_before, $report.overlays_after)
}
else {
    Write-Host '（单人对局：本地玩家就是 AI，跳过真人侧断言）'
}
Write-Host ("报告         : {0}" -f $script:ReportPath)
if ($report.log) { $report.log | ForEach-Object { Write-Host ("  日志: " + $_) -ForegroundColor DarkGray } }
Write-Host ''
if ($report.pass) {
    Write-Host 'PASS：删牌只动了 AI 自己' -ForegroundColor Green
    $script:ExitCode = 0
}
else {
    Write-Host 'FAIL：' -ForegroundColor Red
    $report.failed_assertions | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
}
Write-Host ''

# ---- 6) 收尾 ------------------------------------------------------------------
if (-not $KeepGame) {
    Write-Step '放弃这局测试存档并关闭游戏'
    try { Invoke-Bridge 'sts2_abandon' | Out-Null } catch { }
    Start-Sleep -Seconds 1
    try { Invoke-Bridge 'sts2_quit_app' | Out-Null } catch { }
    # 以「桥接是否还在」判断游戏是否真的退出：机器上可能残留无窗口的 Steam 启动壳进程。
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and (Test-Bridge)) { Start-Sleep -Milliseconds 800 }
    if (Test-Bridge) {
        Write-Host '[test] 游戏没自动关掉，请手动关闭窗口。' -ForegroundColor Yellow
    }
}

exit $script:ExitCode
