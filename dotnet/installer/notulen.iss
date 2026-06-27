; Inno Setup-script voor Notulen — maakt Notulen-Setup.exe.
; Installeert zonder adminrechten (per gebruiker) met snelkoppelingen.
; Stil installeren (voor GPO/Intune): Notulen-Setup.exe /VERYSILENT /NORESTART

#define MyAppName "Scriba"
#define MyAppVersion "1.0.0"
#define MyAppExe "Scriba.exe"
#define MyAppPublisher "Intern"

[Setup]
AppId={{8F4A1E22-6B3D-4C7E-9A11-2D5C7E9B1A30}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Scriba
DefaultGroupName=Scriba
DisableProgramGroupPage=yes
OutputBaseFilename=Scriba-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExe}

[Languages]
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "Snelkoppeling op het bureaublad"; GroupDescription: "Extra snelkoppelingen:"

[Files]
; De volledige self-contained publish-map (Notulen.exe + runtimes\ ernaast).
Source: "..\Notulen\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Scriba"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\Scriba verwijderen"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Scriba"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Scriba nu starten"; Flags: nowait postinstall skipifsilent
