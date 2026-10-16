; Instalador de SqlTableSync (Inno Setup 6).
; Compilar con:  ISCC.exe /DAppVersion=1.0.0 /DPublishDir=..\publish\win-x64 installer\SqlTableSync.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

[Setup]
AppId={{7C3E5B2A-4E1D-4C7B-9A43-2F6B8D1E0A55}
AppName=SqlTableSync
AppVersion={#AppVersion}
AppPublisher=SqlTableSync
DefaultDirName={autopf}\SqlTableSync
DefaultGroupName=SqlTableSync
UninstallDisplayIcon={app}\SqlTableSync.exe
OutputDir=..\dist
OutputBaseFilename=SqlTableSync-Setup-{#AppVersion}
SetupIconFile=..\src\SqlTableSync.App\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Permite instalar sin ser administrador (en el perfil del usuario).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SqlTableSync"; Filename: "{app}\SqlTableSync.exe"
Name: "{group}\{cm:UninstallProgram,SqlTableSync}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SqlTableSync"; Filename: "{app}\SqlTableSync.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SqlTableSync.exe"; Description: "{cm:LaunchProgram,SqlTableSync}"; Flags: nowait postinstall skipifsilent
