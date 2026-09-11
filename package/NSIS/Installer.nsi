; DLL compiled from https://github.com/aranor01/FindProcDLL
!addplugindir /x86-ansi "plugins\x86-ansi"
!addplugindir /x86-unicode "plugins\x86-unicode"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "StrContains.nsh"
!include "FileFunc.nsh"

; define name of installer
OutFile "installer.exe"

; Its own key, so this fork appears in Apps & features as its own entry and cannot take over the
; original's. Installing this must never uninstall a copy of the app somebody else built.
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Swapshelf"

; What this app was called before, so an upgrade can remove the copy it left behind. Kept as its own
; define rather than written inline, because it is the sort of string that gets half-updated.
!define PREVIOUS_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\DLSS Swapper (dkflint723)"

!define UninstLog "uninstall.log"
Var UninstLog

Var DEFAULT_INSTALL_PATH

Function .onInit
  ; Set default install location
  StrCpy $INSTDIR "$PROGRAMFILES64\Swapshelf\"
  ; The missing \ is intentional
  StrCpy $DEFAULT_INSTALL_PATH "$PROGRAMFILES64\Swapshelf"
  ClearErrors
  ReadRegStr $0 SHCTX "${UNINST_KEY}" "InstallLocation"
  ${If} ${Errors}
    ; No-op
  ${Else}
    StrCpy $INSTDIR "$0\"
  ${EndIf}

  ; Stop, rather than say something and carry on regardless. This used to show the message and
  ; then install over the running app anyway, which is not a warning, it is a note about what is
  ; being ignored. Two releases were installed that way and both left the version in Add or remove
  ; programs reading 3.0.2.0 while the files on disk were newer - the same install run silently
  ; wrote every other value in that key correctly, so the failure was partial and invisible.
  ; Whatever the exact mechanism, copying an executable over itself while it runs has no good
  ; version, and the uninstaller has always refused for the same reason.
  ;
  ; MB_OK rather than a retry prompt because a silent install has nobody to ask. /SD IDOK dismisses
  ; it and the Quit below still runs, so an unattended install fails with an error level instead of
  ; half applying.
  FindProcDLL::FindProc "Swapshelf.exe"

  StrCmp $R0 0 NotRunning
    MessageBox MB_OK|MB_ICONSTOP "Swapshelf is currently running. Please close it before installing." /SD IDOK
    SetErrorLevel 2
    Quit
  NotRunning:

  ; The command line is installed beside the app and gets overwritten with it, so it is the same
  ; problem. It is not the same shape of process though: the Steam plugin runs it, it answers, and
  ; it exits, so a single look can catch one on its way out and refuse an install for a process
  ; that has already gone. Looked at twice with a pause between - a transient one is finished by
  ; the second look, and one still there is genuinely in use.
  FindProcDLL::FindProc "swapshelf-cli.exe"
  StrCmp $R0 0 CliNotRunning

  Sleep 2500
  FindProcDLL::FindProc "swapshelf-cli.exe"
  StrCmp $R0 0 CliNotRunning
    MessageBox MB_OK|MB_ICONSTOP "The Swapshelf command line is currently running, which usually means Steam is asking it something. Please close Steam, or wait a moment, and try again." /SD IDOK
    SetErrorLevel 2
    Quit
  CliNotRunning:
FunctionEnd

; On uninstall, confirm you want to remove downloaded/imported DLSS files.
Function un.onInit
  
  FindProcDLL::FindProc "Swapshelf.exe"
  StrCmp $R0 0 NotRunning
    MessageBox MB_OK|MB_ICONSTOP "Swapshelf is currently running. Please close it before attempting to uninstall." /SD IDOK
    SetErrorLevel 2
    Quit
  NotRunning:

  ; Deleted by the same uninstall, so it gets the same check the installer does, including the
  ; second look - Steam can invoke it at any moment and a one second process should not be the
  ; reason an uninstall fails.
  FindProcDLL::FindProc "swapshelf-cli.exe"
  StrCmp $R0 0 UnCliNotRunning

  Sleep 2500
  FindProcDLL::FindProc "swapshelf-cli.exe"
  StrCmp $R0 0 UnCliNotRunning
    MessageBox MB_OK|MB_ICONSTOP "The Swapshelf command line is currently running, which usually means Steam is asking it something. Please close Steam, or wait a moment, and try again." /SD IDOK
    SetErrorLevel 2
    Quit
  UnCliNotRunning:

  ; The wording follows what the uninstaller actually does: it does not delete the dll library.
  ;
  ; The reason changed with the rename and the message had to change with it. That folder used to
  ; be shared with the original DLSS Swapper, so deleting it would have taken another app's files;
  ; now it is Swapshelf's own, under %LOCALAPPDATA%\Swapshelf. It is still kept, but for a different
  ; reason: it holds every dll that was downloaded or imported, and the copies of what each game
  ; shipped with. Those originals are the one thing nothing can recreate, and somebody uninstalling
  ; an app is not necessarily telling it to throw away the only way back for their games.
  MessageBox MB_YESNO "Are you sure you want to uninstall $(^Name)?$\r$\n$\r$\nYour downloaded and imported dlls are kept, along with the copies of what each game originally shipped with, so reinstalling finds them again. Changes already made to your games stay as they are." /SD IDYES IDYES NoAbort
    Abort
  NoAbort:
FunctionEnd

; The install directory should be one of ours, and if it is not we add a folder so that it is.
;
; See issue #169 for why: uninstalling deletes what it installed out of the directory it was given,
; so somebody who points this at a folder that already holds their own files gets those caught up in
; it. Appending a folder means the uninstall can only ever reach into one we made.
;
; The needle is "Swapshelf" rather than "dlss", and the case matters. StrContains is case sensitive,
; and the old needle was lowercase while the folder it was meant to match was "DLSS Swapper" - so it
; never matched, and this appended a folder to every path it was asked about. It goes unnoticed
; because a silent install shows no directory page and so never calls this.
Function .onVerifyInstDir
  ${StrContains} $0 "Swapshelf" $INSTDIR
  StrCmp $0 "" badPath
    Goto done
  badPath:
    StrCpy $INSTDIR "$INSTDIR\Swapshelf\"
  done:
FunctionEnd


Function OnInstFilesPre
  ; The same rule as .onVerifyInstDir, applied again on the way into the install page in case the
  ; path arrived some other way. See the note there about the needle and its case.
  ${StrContains} $0 "Swapshelf" $INSTDIR
  StrCmp $0 "" badPath
    Goto done
  badPath:
    StrCpy $INSTDIR "$INSTDIR\Swapshelf\"
    MessageBox MB_OK "Install path updated to $INSTDIR"
  done:
FunctionEnd


; This is disabled until I can figure out how to make it launch as admin
; Used to launch Swapshelf after install is complete.
;Function LaunchLink
;  ExecShell "" "$SMPROGRAMS\Swapshelf.lnk"
;FunctionEnd


; For removing Start Menu shortcut in Windows 7
; RequestExecutionLevel user
RequestExecutionLevel highest


; App version information. Named and versioned as this fork throughout, so nothing the installer
; shows — its title, its file properties, the entry it writes — claims to be the original or a
; release the original made.
;
; This fork counts its own versions from 2.0.0.0 and does not track upstream's number. It used to
; be "upstream's version with our count in the fourth part", which collided the moment upstream
; released a 1.2.6.0 of its own — two different builds with one number, and this fork's reading as
; older than an upstream release it contains all of. See DLSS Swapper.csproj for the whole rule.
;
; Every version below must match package/config.cmd, src/DLSS Swapper.csproj and src/app.manifest.
; VersionConsistencyTests fails if they drift: they did, and the 1.2.6.0 release shipped an
; installer whose file properties and uninstall entry both said 1.2.5.1.
Name "Swapshelf"
!define MUI_ICON "..\..\src\Assets\icon.ico"
!define MUI_VERSION "3.0.6.1"
!define MUI_PRODUCT "Swapshelf"
VIProductVersion "3.0.6.1"
VIAddVersionKey "ProductName" "Swapshelf"
VIAddVersionKey "ProductVersion" "3.0.6.1"
VIAddVersionKey "FileDescription" "Swapshelf installer"
VIAddVersionKey "FileVersion" "3.0.6.1"
VIAddVersionKey "CompanyName" "dkflint723"
VIAddVersionKey "LegalCopyright" "Fork of beeradmoore/dlss-swapper"

; Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!define MUI_PAGE_CUSTOMFUNCTION_PRE OnInstFilesPre
!insertmacro MUI_PAGE_INSTFILES
 

; These indented statements modify settings for MUI_PAGE_FINISH
!define MUI_FINISHPAGE_NOAUTOCLOSE
;!define MUI_FINISHPAGE_RUN
;!define MUI_FINISHPAGE_RUN_CHECKED
;!define MUI_FINISHPAGE_RUN_TEXT "Launch now"
;!define MUI_FINISHPAGE_RUN_FUNCTION "LaunchLink"
!insertmacro MUI_PAGE_FINISH


; Uninstaller pages
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES


; Languages
!insertmacro MUI_LANGUAGE "English"


!macro CreateDirectoryToInstaller Path
  CreateDirectory "$INSTDIR\${Path}"
  FileWrite $UninstLog "${Path}$\r$\n"
!macroend


!macro AddFileToInstaller FileName FullFileName
  FileWrite $UninstLog "${FileName}$\r$\n"
  File "/oname=${FileName}" "${FullFileName}"
!macroend


Section -openlogfile
  CreateDirectory "$INSTDIR"
  IfFileExists "$INSTDIR\${UninstLog}" +3
    FileOpen $UninstLog "$INSTDIR\${UninstLog}" w
  Goto +4
    SetFileAttributes "$INSTDIR\${UninstLog}" NORMAL
    FileOpen $UninstLog "$INSTDIR\${UninstLog}" a
    FileSeek $UninstLog 0 END
SectionEnd

 
; start default section
Section

  FindProcDLL::FindProc "Swapshelf.exe"
  StrCmp $R0 0 NotRunning
    MessageBox MB_OK|MB_ICONSTOP "Swapshelf is currently running. Please close it and run the installer again." /SD IDOK
    SetErrorLevel 2
    Quit
  NotRunning:

  ; The copy installed before this app was renamed.
  ;
  ; It has its own uninstall key and its own folder, so nothing above finds it, and without this
  ; somebody upgrading ends up with two entries in Apps & features, two Start Menu shortcuts and two
  ; install folders - one of which nothing will ever update again. Its own uninstaller is run rather
  ; than deleting the folder, so it removes exactly what it put there and takes its registry entry
  ; with it.
  ;
  ; Nothing of the user's is at stake here: the library, the downloaded dlls and the saved originals
  ; live under %LOCALAPPDATA%, which the old uninstaller leaves alone and which Storage moves to the
  ; new name on first run.
  ClearErrors
  ReadRegStr $R2 SHCTX "${PREVIOUS_UNINST_KEY}" "InstallLocation"
  ${IfNot} ${Errors}
  ${AndIf} $R2 != ""
    ; That the old executable is in there is what makes the rest of this safe. It proves the path
    ; out of the registry really is the previous install and not some folder that key was pointed at
    ; by hand - which matters, because the last step deletes the folder outright.
    ${If} ${FileExists} "$R2\DLSS Swapper.exe"
      DetailPrint "Removing the previous DLSS Swapper (dkflint723) install..."

      ; Built from InstallLocation rather than run from UninstallString, which is stored with its
      ; own quotes around it - wrapping that in another pair produces "" ... "" and the command
      ; silently does nothing. That is exactly what happened the first time this was written: the
      ; uninstaller never ran, and only the registry entry and the shortcut went, leaving 299 MB of
      ; files behind with nothing left pointing at them.
      ;
      ; _?= runs it in place and waits, instead of copying itself to temp and returning at once.
      ExecWait '"$R2\uninstall.exe" /S _?=$R2'

      ; It cannot delete itself while running in place, and it only removes what its own log lists -
      ; so anything added to that folder afterwards stays. The folder is ours, it has just been
      ; uninstalled, and the check above established which folder it is.
      Delete "$R2\uninstall.exe"
      RMDir /r "$R2"
    ${EndIf}

    DeleteRegKey SHCTX "${PREVIOUS_UNINST_KEY}"
    Delete "$SMPROGRAMS\DLSS Swapper (dkflint723).lnk"
  ${EndIf}

  ; set the installation directory as the destination for the following actions
  SetOutPath $INSTDIR
  
  ; Check if the install already directory exists
  ; We can't just check the directory exists as the directory is created by creating the uninstall.log file
  IfFileExists "$INSTDIR\Swapshelf.exe" InstallProbablyExists Install

  InstallProbablyExists:

    ; If INSTDIR is the default, don't bother promoting to make the upgrade experience easier for existing users. We will just delete it.
    ; This is to fix issues with users using non-default locations and somehow
    ; set their install to C:\Windows\ or something
    StrCmp $INSTDIR $DEFAULT_INSTALL_PATH DeleteOldInstallFiles PromptToDeleteOldInstallFiles
    
    PromptToDeleteOldInstallFiles:
      ; Prompt if it is ok to delete existing directory. This is true by default on silent installs
      MessageBox MB_YESNO|MB_ICONEXCLAMATION 'The directory "$INSTDIR" already exists. Existing app will be uninstalled. Your existing imported and downloaded DLLs will remain. Do you want to continue?' /SD IDYES IDYES DeleteOldInstallFiles
      Quit

    ; Delete the existing install directory
    DeleteOldInstallFiles:
      RMDir /r "$INSTDIR"

  Install:

  ; Adds files from list that was auto-generated by build_Installer.ps1
  !include "FileList.nsh"
  
  ; create the uninstaller
  WriteUninstaller "$INSTDIR\uninstall.exe"
  FileWrite $UninstLog "uninstall.exe$\r$\n"

  ; Calculate install size. This will be updated in app to include data from LOCALAPPDATA\Swapshelf
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  
  # create a shortcut named "new shortcut" in the start menu programs directory
  # point the new shortcut at the program uninstaller
  CreateShortcut "$SMPROGRAMS\Swapshelf.lnk" "$INSTDIR\Swapshelf.exe"

  WriteRegStr SHCTX "${UNINST_KEY}" "DisplayName" "Swapshelf"
  WriteRegStr SHCTX "${UNINST_KEY}" "DisplayVersion" "3.0.6.1"

  ; Named for whoever built it, with what it was forked from, because this build is not the
  ; original author's work and Apps & features is where someone checks who to hold responsible.
  WriteRegStr SHCTX "${UNINST_KEY}" "Publisher" "dkflint723 (fork of beeradmoore/dlss-swapper)"
  WriteRegStr SHCTX "${UNINST_KEY}" "DisplayIcon" "$\"$INSTDIR\Swapshelf.exe$\""
  WriteRegStr SHCTX "${UNINST_KEY}" "UninstallString" "$\"$INSTDIR\uninstall.exe$\""
  WriteRegStr SHCTX "${UNINST_KEY}" "QuietUninstallString" "$\"$INSTDIR\uninstall.exe$\" /S"
  WriteRegStr SHCTX "${UNINST_KEY}" "InstallLocation" $INSTDIR
  WriteRegDWORD SHCTX "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd


; Close the log file off and set it as a readonly hidden system file.
Section -closelogfile
  FileClose $UninstLog
  SetFileAttributes "$INSTDIR\${UninstLog}" READONLY|SYSTEM|HIDDEN
SectionEnd


; uninstaller section start
Section "Uninstall"

  ;Can't uninstall if uninstall log is missing!
  IfFileExists "$INSTDIR\${UninstLog}" +3
    MessageBox MB_OK|MB_ICONSTOP "${UninstLog} not found.$\r$\nUninstallation cannot proceed."
      Abort
 
  Push $R0
  Push $R1
  Push $R2
  SetFileAttributes "$INSTDIR\${UninstLog}" NORMAL
  FileOpen $UninstLog "$INSTDIR\${UninstLog}" r
  StrCpy $R1 -1
 
  GetLineCount:
    ClearErrors
    FileRead $UninstLog $R0
    IntOp $R1 $R1 + 1
    StrCpy $R0 $R0 -2
    Push $R0   
    IfErrors 0 GetLineCount
 
  Pop $R0
 
  LoopRead:
    StrCmp $R1 0 LoopDone
    Pop $R0
 
    IfFileExists "$INSTDIR\$R0\*.*" 0 +3
      RMDir "$INSTDIR\$R0"  #is dir
    Goto +3
    IfFileExists "$INSTDIR\$R0" 0 +2
      Delete "$INSTDIR\$R0" #is file

    IntOp $R1 $R1 - 1
    Goto LoopRead
  LoopDone:
  FileClose $UninstLog
  Delete "$INSTDIR\${UninstLog}"
  RMDir "$INSTDIR"
  Pop $R2
  Pop $R1
  Pop $R0

  ; Downloaded and imported dlls are deliberately LEFT ALONE by this fork's uninstaller.
  ;
  ; The original writes them to LOCALAPPDATA\Swapshelf and so does this build, which means the
  ; two installs share one library, one database and one set of settings. Deleting that folder
  ; here would take the original's dlls with it — uninstalling the fork would quietly gut an app
  ; the user never asked to touch.
  ;
  ; The cost is that removing this leaves the folder behind if it is the only copy installed. That
  ; is the safe direction to be wrong in: files left on disk can be deleted, files deleted from
  ; under another program cannot be got back.

  ; Remove registry keys
  DeleteRegKey SHCTX "${UNINST_KEY}"

  ; Remove start menu shortcut.
  Delete "$SMPROGRAMS\Swapshelf.lnk"

SectionEnd
