; Universal per-user installer. Compile on Windows, Linux or macOS with NSIS 3.
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

!ifndef VERSION
  !error "VERSION must be the three-part app version."
!endif
!ifndef X64_DIR
  !error "X64_DIR must contain the complete x64 portable package."
!endif
!ifndef ARM64_DIR
  !error "ARM64_DIR must contain the complete ARM64 portable package."
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE must be the destination installer EXE."
!endif

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\WinPalm"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
Name "WinPalm"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\WinPalm"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
ManifestDPIAware true
ManifestLongPathAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 32
SetDatablockOptimize on
CRCCheck force
BrandingText "RS Application"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1033 "ProductName" "WinPalm Setup"
VIAddVersionKey /LANG=1033 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${VERSION}.0"
VIAddVersionKey /LANG=1033 "FileDescription" "WinPalm installer for x64 and ARM64 Windows"
VIAddVersionKey /LANG=1033 "CompanyName" "RS Application"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright RS Application. AGPL-3.0-only."
!define MUI_ICON "..\windows\WinPalm\Resources\WinPalm.ico"
!define MUI_UNICON "..\windows\WinPalm\Resources\WinPalm.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Your PC, in your palm."
!define MUI_WELCOMEPAGE_TEXT "Install WinPalm, scan its QR code with your phone and control your PC. This installer chooses the native x64 or ARM64 app automatically.$\r$\n$\r$\nWinPalm installs for your Windows account. It includes its runtime and does not need administrator access."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateInstallDirectory
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\WinPalm.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open WinPalm"
!define MUI_FINISHPAGE_LINK "Visit winpalm.rsapplication.com"
!define MUI_FINISHPAGE_LINK_LOCATION "https://winpalm.rsapplication.com"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Var NativeArchitecture

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "WinPalm needs 64-bit Windows 10 version 1809 or later. Windows 11 is recommended."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastBuild} 17763
    MessageBox MB_OK|MB_ICONSTOP "Update Windows to version 1809 or later before installing WinPalm."
    Abort
  ${EndIf}
  ; IsWow64Process2 returns the OS native machine, including under x86 emulation.
  System::Call 'kernel32::GetCurrentProcess() p.r0'
  ; The API writes USHORT values into these DWORD-sized System plug-in slots.
  ; Zero the whole slots so their upper bytes cannot affect the comparison.
  System::Call 'kernel32::IsWow64Process2(p r0, *i0.r1, *i0.r2) i.r3'
  ${If} $3 == 0
    MessageBox MB_OK|MB_ICONSTOP "Windows could not identify this PC's processor. The installation has not started."
    Abort
  ${EndIf}
  ${If} $2 == 43620 ; IMAGE_FILE_MACHINE_ARM64
    StrCpy $NativeArchitecture "arm64"
  ${ElseIf} $2 == 34404 ; IMAGE_FILE_MACHINE_AMD64
    StrCpy $NativeArchitecture "x64"
  ${Else}
    MessageBox MB_OK|MB_ICONSTOP "WinPalm supports x64 and ARM64 PCs. There is no 32-bit app."
    Abort
  ${EndIf}
FunctionEnd

Function ValidateInstallDirectory
  ; A directory selected by the owner can already contain personal files.
  ; Only replace a recognizable WinPalm package or use a new/empty folder.
  IfFileExists "$INSTDIR\WinPalm.exe" 0 check_contents
  IfFileExists "$INSTDIR\winpalm_core.dll" safe_directory check_contents
  check_contents:
  ClearErrors
  FindFirst $0 $1 "$INSTDIR\*"
  IfErrors safe_directory
  check_next:
    StrCmp $1 "." next_entry
    StrCmp $1 ".." next_entry
    StrCmp $1 "" empty_directory
    FindClose $0
    MessageBox MB_OK|MB_ICONSTOP "Choose a new, empty folder or the folder containing an existing WinPalm installation. This folder contains other files."
    Abort
  next_entry:
    FindNext $0 $1
    IfErrors empty_directory check_next
  empty_directory:
    FindClose $0
  safe_directory:
FunctionEnd

; The owner starting installation/uninstallation authorizes closing their
; current tray app. A registered message lets WinPalm shut down gracefully.
!macro CloseWinPalm PREFIX
Function ${PREFIX}CloseWinPalm
  FindWindow $0 "" "WinPalm"
  ${If} $0 != 0
    System::Call 'user32::RegisterWindowMessageW(w "RSApplication.WinPalm.QuitForInstall") i.r1'
    SendMessage $0 $1 0 0 /TIMEOUT=5000
    StrCpy $2 0
    ${Do}
      Sleep 200
      FindWindow $0 "" "WinPalm"
      ${If} $0 == 0
        ${ExitDo}
      ${EndIf}
      IntOp $2 $2 + 1
    ${LoopUntil} $2 >= 50
    ${If} $0 != 0
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "WinPalm is still running. Quit WinPalm from its notification-area menu, then choose Retry." IDRETRY retry
      Abort
      retry:
      Call ${PREFIX}CloseWinPalm
    ${EndIf}
  ${EndIf}
FunctionEnd
!macroend
!insertmacro CloseWinPalm ""
!insertmacro CloseWinPalm "un."

Section "WinPalm (required)" AppFiles
  SectionIn RO
  Call ValidateInstallDirectory
  Call CloseWinPalm
  ; All packaged files are owned by WinPalm. Private pairings/settings live
  ; elsewhere under LOCALAPPDATA and are never touched by the installer.
  RMDir /r "$INSTDIR\web"
  RMDir /r "$INSTDIR\Resources"
  SetOutPath "$INSTDIR"
  ${If} $NativeArchitecture == "arm64"
    File /r "${ARM64_DIR}\*"
  ${Else}
    File /r "${X64_DIR}\*"
  ${EndIf}
  File /oname=NSIS-LICENSE.txt "NSIS-LICENSE.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\WinPalm"
  CreateShortcut "$SMPROGRAMS\WinPalm\WinPalm.lnk" "$INSTDIR\WinPalm.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "WinPalm"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "RS Application"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\WinPalm.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://winpalm.rsapplication.com"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLUpdateInfo" "https://github.com/rahatsagor/WinPalm/releases"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section /o "Desktop shortcut" DesktopShortcut
  CreateShortcut "$DESKTOP\WinPalm.lnk" "$INSTDIR\WinPalm.exe"
SectionEnd

Section /o "Start WinPalm when I sign in" StartAtLogin
  WriteRegStr HKCU "${RUN_KEY}" "WinPalm" '$\"$INSTDIR\WinPalm.exe$\" --start-minimized'
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  Call un.CloseWinPalm
  Delete "$INSTDIR\WinPalm.exe"
  Delete "$INSTDIR\winpalm_core.dll"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\NOTICE"
  Delete "$INSTDIR\THIRD_PARTY_NOTICES.md"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\SECURITY.md"
  Delete "$INSTDIR\Test-WinPalm.cmd"
  Delete "$INSTDIR\NSIS-LICENSE.txt"
  RMDir /r "$INSTDIR\web"
  RMDir /r "$INSTDIR\Resources"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$DESKTOP\WinPalm.lnk"
  Delete "$SMPROGRAMS\WinPalm\WinPalm.lnk"
  RMDir "$SMPROGRAMS\WinPalm"
  ReadRegStr $0 HKCU "${RUN_KEY}" "WinPalm"
  ${If} $0 == '$\"$INSTDIR\WinPalm.exe$\" --start-minimized'
    DeleteRegValue HKCU "${RUN_KEY}" "WinPalm"
  ${EndIf}
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  RMDir "$INSTDIR" ; succeeds only when no unrelated files remain
SectionEnd
