; =============================================================================
; RefreshSwitcher Setup
; Version 1.8.0, October 2026
;
; Author:   SmokingTux
; Contact:  issues page of the RefreshSwitcher repository on GitHub
;
; Description:
;   NSIS script that builds RefreshSwitcher-Setup-<version>.exe. The setup
;     * installs RefreshSwitcher.exe to Program Files\RefreshSwitcher (admin-protected
;       folder, so other programs cannot replace the exe that autostart launches),
;     * registers the app under "Apps & Features" with SmokingTux as publisher,
;     * optionally adds Start menu and desktop shortcuts and autostart,
;     * closes a running instance cleanly before installing or uninstalling,
;     * moves older installs from Program Files\SmokingTux\RefreshSwitcher,
;     * shows its pages in English, German, Spanish, French or Ukrainian.
;   Files needed next to this script: RefreshSwitcher.exe, app.ico, header.bmp, wizard.bmp.
;
; Build:
;   makensis installer.nsi        (NSIS 3, on Windows: winget install NSIS.NSIS)
;
; Legal:
;   Copyright (C) 2026 SmokingTux
;   SPDX-License-Identifier: GPL-3.0-or-later
;   Free software under the GNU General Public License v3.0 or later. See LICENSE.
; =============================================================================

Unicode true                ; needed for the Ukrainian texts
ManifestDPIAware true       ; sharp setup window on high-DPI screens
SetCompressor /SOLID lzma   ; smallest setup file
RequestExecutionLevel admin ; writing to Program Files and HKLM needs admin rights

; Names and paths used throughout the script
!define APP        "RefreshSwitcher"
!define VERSION    "1.8.0"
!define PUBLISHER  "SmokingTux"
!define EXE        "RefreshSwitcher.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"   ; "Apps & Features" entry
!define RUN_KEY    "Software\Microsoft\Windows\CurrentVersion\Run"                  ; per-user autostart

Name "${APP}"
OutFile "${APP}-Setup-${VERSION}.exe"
InstallDir "$PROGRAMFILES64\${APP}"
BrandingText "${PUBLISHER}"   ; replaces "Nullsoft Install System" at the bottom of the window

!include "MUI2.nsh"      ; Modern UI 2: wizard pages, header images, languages
!include "FileFunc.nsh"  ; GetSize: installed size shown in "Apps & Features"

; ---------- Look

!define MUI_ICON   "app.ico"
!define MUI_UNICON "app.ico"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP   "header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "header.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP   "wizard.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "wizard.bmp"
!define MUI_ABORTWARNING           ; ask before cancelling the setup
!define MUI_COMPONENTSPAGE_NODESC  ; component names are self-explanatory, no description box

; ---------- Pages

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
; "Launch RefreshSwitcher" checkbox on the finish page, started through LaunchApp (see below)
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "$(RunApp)"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; ---------- Languages
; English comes first and is the fallback; otherwise the setup picks the Windows display language automatically.
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "German"
!insertmacro MUI_LANGUAGE "Spanish"
!insertmacro MUI_LANGUAGE "French"
!insertmacro MUI_LANGUAGE "Ukrainian"

; Texts of the components page, the finish page and the message boxes, one line per language
LangString SecApp          ${LANG_ENGLISH} "RefreshSwitcher (required)"
LangString SecApp          ${LANG_GERMAN}  "RefreshSwitcher (erforderlich)"
LangString SecApp          ${LANG_SPANISH}   "RefreshSwitcher (obligatorio)"
LangString SecApp          ${LANG_FRENCH}    "RefreshSwitcher (requis)"
LangString SecApp          ${LANG_UKRAINIAN} "RefreshSwitcher (обов’язково)"
LangString SecStart        ${LANG_ENGLISH} "Start menu shortcut"
LangString SecStart        ${LANG_GERMAN}  "Startmenü-Verknüpfung"
LangString SecStart        ${LANG_SPANISH}   "Acceso directo en el menú Inicio"
LangString SecStart        ${LANG_FRENCH}    "Raccourci dans le menu Démarrer"
LangString SecStart        ${LANG_UKRAINIAN} "Ярлик у меню «Пуск»"
LangString SecDesktop      ${LANG_ENGLISH} "Desktop shortcut"
LangString SecDesktop      ${LANG_GERMAN}  "Desktop-Verknüpfung"
LangString SecDesktop      ${LANG_SPANISH}   "Acceso directo en el escritorio"
LangString SecDesktop      ${LANG_FRENCH}    "Raccourci sur le bureau"
LangString SecDesktop      ${LANG_UKRAINIAN} "Ярлик на робочому столі"
LangString SecAutostart    ${LANG_ENGLISH} "Start with Windows"
LangString SecAutostart    ${LANG_GERMAN}  "Mit Windows starten"
LangString SecAutostart    ${LANG_SPANISH}   "Iniciar con Windows"
LangString SecAutostart    ${LANG_FRENCH}    "Démarrer avec Windows"
LangString SecAutostart    ${LANG_UKRAINIAN} "Запускати разом із Windows"
LangString RunApp          ${LANG_ENGLISH} "Launch RefreshSwitcher"
LangString RunApp          ${LANG_GERMAN}  "RefreshSwitcher starten"
LangString RunApp          ${LANG_SPANISH}   "Iniciar RefreshSwitcher"
LangString RunApp          ${LANG_FRENCH}    "Lancer RefreshSwitcher"
LangString RunApp          ${LANG_UKRAINIAN} "Запустити RefreshSwitcher"
LangString NeedNet         ${LANG_ENGLISH} "RefreshSwitcher needs .NET Framework 4.5 or newer, which was not found.$\r$\nInstall it from microsoft.com and run setup again."
LangString NeedNet         ${LANG_GERMAN}  "RefreshSwitcher braucht .NET Framework 4.5 oder neuer, das nicht gefunden wurde.$\r$\nInstalliere es von microsoft.com und starte das Setup erneut."
LangString NeedNet         ${LANG_SPANISH}   "RefreshSwitcher necesita .NET Framework 4.5 o posterior, que no se encontró.$\r$\nInstálalo desde microsoft.com y vuelve a ejecutar el instalador."
LangString NeedNet         ${LANG_FRENCH}    "RefreshSwitcher nécessite .NET Framework 4.5 ou plus récent, introuvable.$\r$\nInstallez-le depuis microsoft.com puis relancez l’installation."
LangString NeedNet         ${LANG_UKRAINIAN} "RefreshSwitcher потребує .NET Framework 4.5 або новішої версії, яку не знайдено.$\r$\nВстановіть її з microsoft.com і запустіть інсталятор знову."
LangString RemoveSettings  ${LANG_ENGLISH} "Also delete your RefreshSwitcher settings and log?"
LangString RemoveSettings  ${LANG_GERMAN}  "Auch deine RefreshSwitcher-Einstellungen und das Protokoll löschen?"
LangString RemoveSettings  ${LANG_SPANISH}   "¿Eliminar también la configuración y el registro de RefreshSwitcher?"
LangString RemoveSettings  ${LANG_FRENCH}    "Supprimer aussi les paramètres et le journal de RefreshSwitcher ?"
LangString RemoveSettings  ${LANG_UKRAINIAN} "Також видалити налаштування та журнал RefreshSwitcher?"

; ---------- Version info of the setup file (right-click > Properties > Details)

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=${LANG_ENGLISH} "ProductName"     "${APP}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "CompanyName"     "${PUBLISHER}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "LegalCopyright"  "© 2026 ${PUBLISHER}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "FileDescription" "${APP} Setup"
VIAddVersionKey /LANG=${LANG_ENGLISH} "FileVersion"     "${VERSION}"
VIAddVersionKey /LANG=${LANG_ENGLISH} "ProductVersion"  "${VERSION}"

; ---------- Closing a running instance

; Asks a running RefreshSwitcher to exit cleanly ("--exit" restores the game rate on all monitors),
; then kills any instance that is still running, e.g. one started from another folder.
; "+3" skips the clean exit if the exe is not installed yet. Comments do not count as instructions.
!macro StopApp
  IfFileExists "$INSTDIR\${EXE}" 0 +3
    ExecWait '"$INSTDIR\${EXE}" --exit'
    Sleep 1500
  nsExec::Exec 'taskkill /F /IM ${EXE}'
  Pop $0
!macroend

Function .onInit
  SetRegView 64
  ; Previous install location (for example Program Files\SmokingTux\RefreshSwitcher up to 1.2), used to migrate it
  ReadRegStr $R9 HKLM "${UNINST_KEY}" "InstallLocation"
  ; Check for .NET Framework 4.5 or newer: the "Release" value is 378389 or higher.
  ; If it is missing or lower, show a message and stop before anything is installed.
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  IntCmp $0 378389 net_ok 0 net_ok
    MessageBox MB_OK|MB_ICONSTOP "$(NeedNet)" /SD IDOK
    Abort
  net_ok:
FunctionEnd

; Starts the app through Explorer so it runs with normal user rights, not with the setup's admin rights.
; This matters because the app writes per-user settings and the per-user autostart entry.
Function LaunchApp
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${EXE}"'
FunctionEnd

; ---------- Installation

; Main component, always installed (SectionIn RO = read-only checkbox)
Section "!$(SecApp)" SecApp
  SectionIn RO

  ; If an older install exists, ask that copy to exit cleanly first ("+4" skips this when $R9 is empty)
  StrCmp $R9 "" +4
  IfFileExists "$R9\${EXE}" 0 +3
    ExecWait '"$R9\${EXE}" --exit'
    Sleep 1500
  !insertmacro StopApp

  ; Remove the old install if it lives in a different folder than the new one
  StrCmp $R9 "" migrate_done
  StrCmp $R9 "$INSTDIR" migrate_done
    Delete "$R9\${EXE}"
    Delete "$R9\uninstall.exe"
    RMDir "$R9"
    RMDir "$PROGRAMFILES64\${PUBLISHER}"
  migrate_done:

  ; Copy the program and create the uninstaller
  SetOutPath "$INSTDIR"
  File "${EXE}"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Entry in "Apps & Features" (64-bit registry view, publisher, icon, size, uninstall commands)
  SetRegView 64
  WriteRegStr   HKLM "${UNINST_KEY}" "DisplayName"          "${APP}"
  WriteRegStr   HKLM "${UNINST_KEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   HKLM "${UNINST_KEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   HKLM "${UNINST_KEY}" "DisplayIcon"          '"$INSTDIR\${EXE}"'
  WriteRegStr   HKLM "${UNINST_KEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   HKLM "${UNINST_KEY}" "UninstallString"      '"$INSTDIR\uninstall.exe"'
  WriteRegStr   HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
  ; Installed size in KB for "Apps & Features"
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"

  ; If autostart already exists (for example pointing to a copy in Downloads), point it to the new path.
  ; "+2" skips the update when no autostart entry exists.
  ReadRegStr $0 HKCU "${RUN_KEY}" "${APP}"
  StrCmp $0 "" +2
    WriteRegStr HKCU "${RUN_KEY}" "${APP}" '"$INSTDIR\${EXE}"'
SectionEnd

; Start menu shortcut for all users (selected by default)
Section "$(SecStart)" SecStart
  SetShellVarContext all
  CreateShortcut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
SectionEnd

; Desktop shortcut for all users (/o = not selected by default)
Section /o "$(SecDesktop)" SecDesktop
  SetShellVarContext all
  CreateShortcut "$DESKTOP\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
SectionEnd

; Autostart for the user who confirmed the UAC prompt (selected by default)
Section "$(SecAutostart)" SecAutostart
  WriteRegStr HKCU "${RUN_KEY}" "${APP}" '"$INSTDIR\${EXE}"'
SectionEnd

; ---------- Uninstallation

; The uninstall key was written to the 64-bit registry view, so read it from there too
Function un.onInit
  SetRegView 64
FunctionEnd

Section "Uninstall"
  ; Close the app first so its exe can be deleted and the monitors return to the game rate
  !insertmacro StopApp

  ; Shortcuts, autostart, program files and the "Apps & Features" entry
  SetShellVarContext all
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$DESKTOP\${APP}.lnk"

  DeleteRegValue HKCU "${RUN_KEY}" "${APP}"

  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"

  SetRegView 64
  DeleteRegKey HKLM "${UNINST_KEY}"

  ; User settings and log live in %APPDATA%\RefreshSwitcher and are only removed on request.
  ; Default answer is "No"; a silent uninstall (/S) keeps them as well. "+2" skips the deletion on "No".
  SetShellVarContext current
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "$(RemoveSettings)" /SD IDNO IDNO +2
    RMDir /r "$APPDATA\${APP}"
SectionEnd
