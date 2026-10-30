@echo off
rem Uses the C# compiler that ships with Windows (.NET Framework 4.8) - nothing to install.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:System.Web.Extensions.dll
if not exist bin mkdir bin
"%CSC%" /nologo /nowarn:649 /define:SELFTEST /target:exe /out:bin\selftest.exe %REFS% src\Tree.cs src\Layout.cs src\Keys.cs || exit /b 1
bin\selftest.exe || exit /b 1
"%CSC%" /nologo /target:winexe /platform:x64 /optimize /out:bin\Tessera.exe %REFS% /resource:src\settings.html,settings.html src\*.cs || exit /b 1
echo Built bin\Tessera.exe
