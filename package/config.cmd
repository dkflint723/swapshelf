@echo off

set app_version=3.0.6.1
set initial_directory=%cd%

set csproj_file=..\src\DLSS Swapper.csproj

REM Published into the same folder as the app rather than beside it, because the
REM Steam plugin looks for it in the install root and because it references the
REM app project - everything it needs is already there.
set cli_csproj_file=..\cli\DLSS.Swapper.Cli\DLSS.Swapper.Cli.csproj

set output_installer=Output\Swapshelf-%app_version%-installer.exe
set output_zip=Output\Swapshelf-%app_version%-portable.zip
