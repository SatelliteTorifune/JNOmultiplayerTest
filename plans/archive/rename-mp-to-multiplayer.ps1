param(
    [switch]$Apply
)

# 把项目里残留的 "mp" 命名统一成 "MultiPlayer"（2026-09-22）。
# 用法：
#   pwsh -File plans/archive/rename-mp-to-multiplayer.ps1          # dry-run，只列将发生的替换
#   pwsh -File plans/archive/rename-mp-to-multiplayer.ps1 -Apply   # 实际写入
#
# 说明：只改 Assets/Scripts 下的 .cs（UTF-8，无 BOM）。按"先长后短"的顺序做字面替换，
# 避免 MpNetworkManager 被短模式先命中。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# 顺序敏感：长 token 在前，短 token 在后
$rules = @(
    # --- 运行时字符串：Harmony id / GameObject 名 / Inspector 名 / PlayerPrefs key ---
    @{ Old = '"MPTest"';                       New = '"MultiPlayer"' }
    @{ Old = '"MPSteamLobbyBrowser"';          New = '"MultiPlayerSteamLobbyBrowser"' }
    @{ Old = '"MPNetwork"';                    New = '"MultiPlayerNetwork"' }
    @{ Old = '"MPUI"';                         New = '"MultiPlayerUI"' }
    @{ Old = '"MP_Remote_"';                   New = '"MultiPlayer_Remote_"' }
    @{ Old = '"MpCraftLoadingIndicator"';      New = '"MultiPlayerCraftLoadingIndicator"' }
    @{ Old = '"MpLoadingSpinner"';             New = '"MultiPlayerLoadingSpinner"' }
    @{ Old = '"MpLoadingText"';                New = '"MultiPlayerLoadingText"' }
    @{ Old = '"Mp.TickRateSlider"';            New = '"MultiPlayer.TickRateSlider"' }
    @{ Old = '"MpTcpAccept"';                  New = '"MultiPlayerTcpAccept"' }
    @{ Old = '"MpTcpClientRecv"';              New = '"MultiPlayerTcpClientRecv"' }
    @{ Old = '"MpTcpPeer"';                    New = '"MultiPlayerTcpPeer"' }
    @{ Old = '"Mptest.UpdateReminder.SkippedVersion"'; New = '"MultiPlayer.UpdateReminder.SkippedVersion"' }
    @{ Old = '"MptestUpdateReminder"';         New = '"MultiPlayerUpdateReminder"' }
    @{ Old = '"aMptestModUpdater/1.0"';        New = '"MultiPlayerModUpdater/1.0"' }
    @{ Old = '"[Mptest][Lobby] "';             New = '"[MultiPlayer][Lobby] "' }
    @{ Old = '"[Mptest][Update] "';            New = '"[MultiPlayer][Update] "' }
    # --- Steam 大厅元数据键（wire：新旧 DLL 不互通，同版本双端无碍）---
    @{ Old = '"mp_name"';        New = '"multiPlayer_name"' }
    @{ Old = '"mp_desc"';        New = '"multiPlayer_desc"' }
    @{ Old = '"mp_owner"';       New = '"multiPlayer_owner"' }
    @{ Old = '"mp_ver_major"';   New = '"multiPlayer_ver_major"' }
    @{ Old = '"mp_ver_minor"';   New = '"multiPlayer_ver_minor"' }
    @{ Old = '"mp_ver_build"';   New = '"multiPlayer_ver_build"' }
    # --- 本地化 key（与 Content/Languages/*.xml 同步改）---
    @{ Old = 'MultiPlayer.MultiPlayerUI.MPinspector';     New = 'MultiPlayer.MultiPlayerUI.MultiPlayerInspector' }
    @{ Old = 'MultiPlayer.MultiPlayerUI.MpButtonTooltip'; New = 'MultiPlayer.MultiPlayerUI.MultiPlayerButtonTooltip' }
    @{ Old = 'OnToggleMPInspectorPanelState';             New = 'OnToggleMultiPlayerInspectorPanelState' }
    # --- 日志前缀：'"MP.' / "MP.xxx / "MP: / "MP xxx ---
    @{ Old = '"MP.';             New = '"MultiPlayer.' }
    @{ Old = '"MP:';             New = '"MultiPlayer:' }
    @{ Old = '"MP ';             New = '"MultiPlayer ' }
    # --- 标识符：类型 / 方法 / 字段 ---
    @{ Old = 'MpNetworkManager';  New = 'MultiPlayerNetworkManager' }
    @{ Old = 'MpPeer';            New = 'MultiPlayerPeer' }
    @{ Old = 'MpMessages';        New = 'MultiPlayerMessages' }
    @{ Old = 'MpSyncUtil';        New = 'MultiPlayerSyncUtil' }
    @{ Old = 'MpUiBottomId';      New = 'MultiPlayerUiBottomId' }
    @{ Old = 'IMpTransport';      New = 'IMultiPlayerTransport' }
    @{ Old = '_mpGameObject';     New = '_multiPlayerGameObject' }
    @{ Old = 'EnsureMpManager';   New = 'EnsureMultiPlayerManager' }
    @{ Old = '_mp';               New = '_multiPlayer' }
    # --- 构造参数 (NetworkManager mp) ---（在 _mp 之后替换，避免先把 mp 吃掉）
    @{ Old = '(NetworkManager mp) { _multiPlayer = mp; }'; New = '(NetworkManager multiPlayer) { _multiPlayer = multiPlayer; }' }
    @{ Old = 'var mp =';          New = 'var multiPlayer =' }
    @{ Old = '_serverPeers[peer.Id] = mp;'; New = '_serverPeers[peer.Id] = multiPlayer;' }
)

$files = Get-ChildItem (Join-Path $root 'Assets\Scripts') -Recurse -File -Filter *.cs
$total = 0
$touched = @{}

foreach ($f in $files) {
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $orig = $text
    foreach ($r in $rules) {
        $count = ([regex]::Matches($text, [regex]::Escape($r.Old))).Count
        if ($count -gt 0) {
            $text = $text.Replace($r.Old, $r.New)
            $total += $count
            if (-not $touched.ContainsKey($r.Old)) { $touched[$r.Old] = @() }
            $touched[$r.Old] += "$($f.FullName.Replace("$root\",''))($count)"
        }
    }
    if ($Apply -and $text -ne $orig) {
        [System.IO.File]::WriteAllText($f.FullName, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
}

foreach ($k in $rules.Old) {
    if ($touched.ContainsKey($k)) {
        "{0,-56} -> {1,-42} x{2}" -f $k, ($rules | Where-Object { $_.Old -eq $k }).New, (($touched[$k] | ForEach-Object { [int]($_ -replace '.*\((\d+)\)$','$1') }) | Measure-Object -Sum).Sum
        ($touched[$k] | Sort-Object) | ForEach-Object { "      $_" }
    }
}
""
if ($Apply) { "APPLIED: $total replacements" } else { "DRY-RUN: $total replacements planned (re-run with -Apply to write)" }
