@echo off
setlocal
REM 一键打包（Release）：编译并把 Dalamud 自动生成的插件包 latest.zip 复制为 dist\NEVERMOVE.zip
set DALAMUD_HOME=C:\Users\Administrator\AppData\Roaming\XIVLauncherCN\addon\Hooks\26-06-27-01

dotnet build -c Release %*
if errorlevel 1 exit /b 1

if not exist "dist" mkdir dist
copy /Y "bin\Release\NEVERMOVE\latest.zip" "dist\NEVERMOVE.zip" >nul

echo.
echo 已生成 dist\NEVERMOVE.zip （含 NEVERMOVE.dll / .json / .deps.json，可直接发布）
echo 下一步：把 dist\NEVERMOVE.zip 上传到 GitHub Release，再把下载直链填进插件仓库的 pluginmaster.json 的 DownloadLinkInstall。
endlocal
