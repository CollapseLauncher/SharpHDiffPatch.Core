@echo off
if /i not exist "artifacts" mkdir "artifacts"
dotnet restore || goto :Fail
dotnet clean -c Release SharpHDiffPatch.Core.csproj || goto :Fail
call :Clean
dotnet build -c Release SharpHDiffPatch.Core.csproj || goto :Fail
dotnet pack -c Release -o artifacts -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg  SharpHDiffPatch.Core.csproj || goto :Fail
goto :Success

:Fail
echo An error occurred while on build/pack process with error code: %errorlevel%
goto :End

:Success
echo Packing has been successful. Exported package is located at "artifacts" folder
call :Clean
goto :End

:End
pause > nul | echo Press any key to quit...
goto :EOF

:Clean
rmdir /S /Q bin
rmdir /S /Q obj