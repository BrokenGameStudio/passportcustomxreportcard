@echo off
echo Compilation de passportcustom...
echo.

dotnet build passportcustom.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary

if %errorlevel% == 0 (
    echo.
    echo OK - Compilation reussie !
    echo Le fichier se trouve dans : bin\Debug\netstandard2.1\passportcustom.dll
) else (
    echo.
    echo ERREUR - La compilation a echoue.
)

pause
