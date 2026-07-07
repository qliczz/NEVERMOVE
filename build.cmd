@echo off
setlocal
REM NEVERMOVE 一键编译（Debug）：自动把 DALAMUD_HOME 指向本机 XIVLauncherCN 的 Dalamud Hooks 目录
REM （和 RaceKnight 同样的做法；csproj 里的 AssemblySearchPaths 也能让直接 dotnet build 生效）
set DALAMUD_HOME=C:\Users\Administrator\AppData\Roaming\XIVLauncherCN\addon\Hooks\26-06-27-01
dotnet build -c Debug %*
endlocal
