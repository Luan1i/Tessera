@echo off
rem Builds Tessera and zips the exe into dist\Tessera-<version>.zip (version from src\AssemblyInfo.cs).
cd /d "%~dp0"
call "%~dp0build.cmd" || exit /b 1
powershell -NoProfile -Command "$v = [Reflection.AssemblyName]::GetAssemblyName('bin\Tessera.exe').Version.ToString(3); New-Item -ItemType Directory -Force dist | Out-Null; Compress-Archive -Force bin\Tessera.exe \"dist\Tessera-$v.zip\"; Write-Host \"Built dist\Tessera-$v.zip\"" || exit /b 1
