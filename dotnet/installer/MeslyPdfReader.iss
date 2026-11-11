; Mesly PDF Reader — Inno Setup 6 kurulum betiği
; Derleme: iscc /DAppVersion=0.2.0 /DPublishDir=..\publish MeslyPdfReader.iss

#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#define AppName "Mesly PDF Reader"
#define AppExe "MeslyPdfReader.exe"
#define ProgId "MeslyPdfReader.Document"

[Setup]
AppId={{6F2B7C1E-4A8D-4E5B-9C3A-6D1F0B2E7A49}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Mesly
AppCopyright=© 2026 Mesly
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Kurulumu
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Yönetici izni gerekmez; kullanıcı isterse tüm kullanıcılar için kurabilir.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=MeslyPdfReader-Setup-{#AppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSizePercent=110
Compression=lzma2/max
SolidCompression=yes
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no
LicenseFile=..\THIRD_PARTY_NOTICES.md

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; GroupDescription: "Ek simgeler:"
Name: "assocpdf"; Description: "PDF dosyalarını “Birlikte aç” listesine ve sağ tık menüsüne ekle"; GroupDescription: "Windows entegrasyonu:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "PDF görüntüleme, sayfa yönetimi ve dönüştürme"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; ProgID
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "PDF Belgesi"; Flags: uninsdeletekey; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "PDF Belgesi"; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assocpdf
; .pdf → Birlikte aç
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assocpdf
; Gezgin sağ tık menüsü
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPdfReader.Open"; ValueType: string; ValueName: ""; ValueData: "Mesly PDF Reader ile aç"; Flags: uninsdeletekey; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPdfReader.Open"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPdfReader.Open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assocpdf
; Windows "Varsayılan uygulamalar" kaydı
Root: HKA; Subkey: "Software\Mesly\PdfReader\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: assocpdf
Root: HKA; Subkey: "Software\Mesly\PdfReader\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "PDF görüntüleme, sayfa yönetimi ve dönüştürme"; Tasks: assocpdf
Root: HKA; Subkey: "Software\Mesly\PdfReader\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "{#ProgId}"; Tasks: assocpdf
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\Mesly\PdfReader\Capabilities"; Flags: uninsdeletevalue; Tasks: assocpdf

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} uygulamasını başlat"; Flags: nowait postinstall skipifsilent

[Code]
// Kullanıcı verileri (%LOCALAPPDATA%\Mesly\PdfReader) kaldırmada sorulur.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Mesly\PdfReader');
    if DirExists(DataDir) and (not UninstallSilent) then
      if MsgBox('Ayarlar, son dosyalar listesi ve geçici dosyalar da silinsin mi?', mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
