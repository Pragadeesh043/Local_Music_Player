Unicode True
RequestExecutionLevel user
SetCompressor zlib

!include "MUI2.nsh"
!include "x64.nsh"

Name "MP4A"
OutFile "${SETUP_FILE}"
InstallDir "$LOCALAPPDATA\Programs\MP4A"
ShowInstDetails show

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\MP4A.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open MP4A"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\README.md"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the setup guide for FFmpeg"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Function .onInit
    ${IfNot} ${RunningX64}
        MessageBox MB_OK|MB_ICONSTOP "MP4A requires a 64-bit version of Windows."
        SetErrorLevel 1
        Quit
    ${EndIf}
FunctionEnd

Section
    SetOutPath "$INSTDIR"
    File /r "${APP_PAYLOAD}\*"
SectionEnd
