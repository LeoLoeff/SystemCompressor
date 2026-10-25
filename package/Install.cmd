@echo off
rem Double-click to install. The PowerShell script asks for administrator rights itself.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
