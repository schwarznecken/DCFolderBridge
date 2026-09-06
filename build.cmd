@echo off
setlocal
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:"%~dp0DCFolderBridge.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll "%~dp0Bridge.cs"
exit /b %errorlevel%
