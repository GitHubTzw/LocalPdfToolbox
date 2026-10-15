@echo off
setlocal
chcp 65001 >nul
if not exist "%~dp0dist\LocalPdfV2\LocalPdfV2.exe" (
  echo 未找到已构建的程序，请先在项目目录运行 build.ps1。
  pause
  exit /b 1
)
start "" "%~dp0dist\LocalPdfV2\LocalPdfV2.exe"
