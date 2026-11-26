Unicode True
RequestExecutionLevel user
SetCompressor zlib

!include "MUI2.nsh"
!include "x64.nsh"

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\MP4A"

Name "MP4A"
OutFile "${SETUP_FILE}"
InstallDir "$LOCALAPPDATA\Programs\MP4A"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
ShowInstDetails show
VIProductVersion "${APP_VERSION}"
VIAddVersionKey "ProductName" "MP4A"
VIAddVersionKey "FileDescription" "MP4A Setup"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "MP4A contributors"

!define MUI_ABORTWARNING
!define MUI_ICON "${APP_PAYLOAD}\Assets\MP4A.ico"
!define MUI_UNICON "${APP_PAYLOAD}\Assets\MP4A.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\MP4A.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open MP4A"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\README.md"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the MP4A setup guide"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
    ${IfNot} ${RunningX64}
        MessageBox MB_OK|MB_ICONSTOP "MP4A requires a 64-bit version of Windows."
        SetErrorLevel 1
        Quit
    ${EndIf}
FunctionEnd

Section
    SetShellVarContext current
    ; Use the previous installer's file list when replacing a registered copy.
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 == $INSTDIR
    ${AndIf} ${FileExists} "$INSTDIR\Uninstall.exe"
        ClearErrors
        ExecWait '"$INSTDIR\Uninstall.exe" /S _?=$INSTDIR' $1
        ${If} ${Errors}
        ${OrIf} $1 != 0
            MessageBox MB_OK|MB_ICONSTOP "Close MP4A and try the upgrade again." /SD IDOK
            SetErrorLevel 1
            Abort
        ${EndIf}
    ${EndIf}

    ; Remove known assets from the older installer that had no uninstall list.
    Delete "$INSTDIR\Glassroom\assets\demo-DcV9PB5m.js"
    Delete "$INSTDIR\Glassroom\assets\index-CPk4cncn.js"
    Delete "$INSTDIR\Glassroom\assets\index-KrmU5OI-.css"
    Delete "$INSTDIR\licenses\nuget\microsoft.net.illink.tasks\10.0.15\THIRD-PARTY-NOTICES.TXT"
    RMDir "$INSTDIR\licenses\nuget\microsoft.net.illink.tasks\10.0.15"
    RMDir "$INSTDIR\licenses\nuget\microsoft.net.illink.tasks"

    ClearErrors
    SetOutPath "$INSTDIR"
    File /r "${APP_PAYLOAD}\*"
    IfErrors install_failed
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateDirectory "$SMPROGRAMS\MP4A"
    CreateShortCut "$SMPROGRAMS\MP4A\MP4A.lnk" "$INSTDIR\MP4A.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "MP4A"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\MP4A.exe,0"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
    IfErrors 0 install_complete
install_failed:
    MessageBox MB_OK|MB_ICONSTOP "MP4A could not be installed. Close the app and try setup again." /SD IDOK
    SetErrorLevel 1
    Abort
install_complete:
SectionEnd

Section "Uninstall"
    SetShellVarContext current
    SetOutPath "$TEMP"
    ClearErrors
    IfFileExists "$INSTDIR\MP4A.exe" 0 +2
    Delete "$INSTDIR\MP4A.exe"
    IfErrors uninstall_failed
    !include "${UNINSTALL_FILES}"

    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 == $INSTDIR
        Delete "$SMPROGRAMS\MP4A\MP4A.lnk"
        RMDir "$SMPROGRAMS\MP4A"
        DeleteRegKey HKCU "${UNINSTALL_KEY}"
    ${EndIf}
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
    SetErrorLevel 0
    Goto uninstall_complete
uninstall_failed:
    MessageBox MB_OK|MB_ICONSTOP "MP4A files could not be removed. Close the app and try again." /SD IDOK
    SetErrorLevel 1
    Abort
uninstall_complete:
SectionEnd
