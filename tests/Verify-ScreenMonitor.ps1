param([string]$Model='gemma3:4b')
# Phase 7 の一件監視も同じ実装で検証する。旧単一 API は利用しない。
& (Join-Path $PSScriptRoot 'Verify-MultiScreenMonitor.ps1') -Model $Model -Single