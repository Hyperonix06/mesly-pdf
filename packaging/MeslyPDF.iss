; Mesly PDF – Inno Setup kurulum betiği (Windows 10 / 11, 64-bit)
; Derleme: iscc packaging\MeslyPDF.iss   (önce PyInstaller ile dist\MeslyPDF oluşturulmalı)

#define AppName "Mesly PDF"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "MeslyPDF.exe"
#define ProgId "MeslyPDF.Document"

[Setup]
AppId={{6E2B9B0E-6C1A-4E7D-9F1B-4D9D8A1C2E55}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Mesly
AppPublisherURL=https://mesm.com.tr
DefaultDirName={autopf}\Mesly PDF
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=MeslyPDF-Kurulum-{#AppVersion}
SetupIconFile=..\meslypdf\assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ChangesAssociations=yes
CloseApplications=yes

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstü simgesi oluştur"; GroupDescription: "Ek simgeler:"
Name: "contextmenu"; Description: "Gezgin sağ tık menüsüne ekle (Aç, Word'e/Excel'e dönüştür, Aranabilir PDF yap, Sıkıştır, Karşılaştır)"; GroupDescription: "Windows entegrasyonu:"
Name: "openwith"; Description: "PDF ve görüntüler için 'Birlikte aç' listesine ekle"; GroupDescription: "Windows entegrasyonu:"
Name: "defaultpdf"; Description: "Varsayılan PDF uygulaması yap"; GroupDescription: "Windows entegrasyonu:"; Flags: unchecked
Name: "sendto"; Description: "'Gönder' menüsüne 'Mesly PDF (birleştir)' ekle"; GroupDescription: "Windows entegrasyonu:"
Name: "autostart"; Description: "Windows açılışında Hot Folder hizmetini başlat (tepside)"; GroupDescription: "Otomasyon:"; Flags: unchecked

[Files]
Source: "..\dist\MeslyPDF\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Mesly PDF – Ekrandan metin yakala"; Filename: "{app}\{#AppExe}"; Parameters: "--tray"
Name: "{group}\{#AppName} kaldır"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Windows\SendTo\Mesly PDF (birleştir)"; Filename: "{app}\{#AppExe}"; Parameters: "--merge"; Tasks: sendto

[Registry]
; ProgID
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueData: "PDF belgesi (Mesly PDF)"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
; Uygulama kaydı (Ayarlar > Varsayılan uygulamalar listesinde görünmesi için)
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "MeslyPDF"; ValueData: "Software\Mesly\MeslyPDF\Capabilities"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Mesly\MeslyPDF\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Mesly\MeslyPDF\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "PDF düzenleme, OCR, dönüştürme ve karşılaştırma"
Root: HKA; Subkey: "Software\Mesly\MeslyPDF\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "{#ProgId}"
; Birlikte aç
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: none; ValueName: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.png\OpenWithProgids"; ValueType: none; ValueName: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.jpg\OpenWithProgids"; ValueType: none; ValueName: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.tif\OpenWithProgids"; ValueType: none; ValueName: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.tiff\OpenWithProgids"; ValueType: none; ValueName: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: openwith
; Varsayılan PDF uygulaması (Windows 10/11 son kararı kullanıcıya sorabilir)
Root: HKA; Subkey: "Software\Classes\.pdf"; ValueType: string; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: defaultpdf
; Sağ tık menüsü – PDF
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF"; ValueType: string; ValueName: "SubCommands"; ValueData: ""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\1open"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF ile aç"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\1open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\2ocr"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Aranabilir PDF yap (OCR)"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\2ocr\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --ocr ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\3word"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Word'e dönüştür"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\3word\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to docx ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\4excel"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Excel'e dönüştür"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\4excel\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to xlsx ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\5compress"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Sıkıştır"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\5compress\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --compress ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\6compare"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Başka bir belgeyle karşılaştır"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\MeslyPDF\shell\6compare\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --compare ""%1"""; Tasks: contextmenu
; Sağ tık menüsü – görüntüler
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Aranabilir PDF yap"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\MeslyPDF.Ocr\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --ocr ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\MeslyPDF.Word"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Word'e dönüştür"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\MeslyPDF.Word"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\MeslyPDF.Word\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to docx ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: OCR Düzenleyicide aç"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\MeslyPDF.Edit\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Aranabilir PDF yap"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\MeslyPDF.Ocr\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --ocr ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\MeslyPDF.Word"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Word'e dönüştür"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\MeslyPDF.Word"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\MeslyPDF.Word\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to docx ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: OCR Düzenleyicide aç"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\MeslyPDF.Edit\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Aranabilir PDF yap"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\MeslyPDF.Ocr\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --ocr ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\MeslyPDF.Word"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Word'e dönüştür"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\MeslyPDF.Word"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\MeslyPDF.Word\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to docx ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: OCR Düzenleyicide aç"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\MeslyPDF.Edit\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Aranabilir PDF yap"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\MeslyPDF.Ocr\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --ocr ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\MeslyPDF.Word"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Word'e dönüştür"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\MeslyPDF.Word"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\MeslyPDF.Word\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to docx ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: OCR Düzenleyicide aç"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\MeslyPDF.Edit\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Aranabilir PDF yap"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\MeslyPDF.Ocr\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --ocr ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\MeslyPDF.Word"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Word'e dönüştür"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\MeslyPDF.Word"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\MeslyPDF.Word\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to docx ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: OCR Düzenleyicide aç"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\MeslyPDF.Edit\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Aranabilir PDF yap"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\MeslyPDF.Ocr"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\MeslyPDF.Ocr\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --ocr ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\MeslyPDF.Word"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: Word'e dönüştür"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\MeslyPDF.Word"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\MeslyPDF.Word\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to docx ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: OCR Düzenleyicide aç"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\MeslyPDF.Edit"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe}"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\MeslyPDF.Edit\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
; Office belgeleri -> PDF
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.docx\shell\MeslyPDF.ToPdf"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: PDF'e dönüştür"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.docx\shell\MeslyPDF.ToPdf\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to pdf ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.xlsx\shell\MeslyPDF.ToPdf"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: PDF'e dönüştür"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.xlsx\shell\MeslyPDF.ToPdf\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to pdf ""%1"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pptx\shell\MeslyPDF.ToPdf"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Mesly PDF: PDF'e dönüştür"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\SystemFileAssociations\.pptx\shell\MeslyPDF.ToPdf\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --to pdf ""%1"""; Tasks: contextmenu
; Oturum açılışı
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MeslyPDFHotFolder"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} uygulamasını başlat"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /f /im {#AppExe}"; Flags: runhidden; RunOnceId: "KillMesly"
