; ==============================================================================
; 知微 · TeachCraft - Inno Setup 安装脚本
; ==============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MyAppBaseVersion
  #define MyAppBaseVersion "1.0.0.0"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "TeachCraft-" + MyAppVersion + "-Setup"
#endif

#ifndef Arch
  #define Arch "x64"
#endif

; 部署模式（sc = 自包含 / fdd = 依赖框架）与完整 RID。
; 用于给「检查更新」留下安装身份记录；六个 Windows 包共用同一 AppId，
; 因此靠 MyAppMode + Arch 把不同 SKU 写到各自的子键，互不覆盖。
#ifndef MyAppMode
  #define MyAppMode "sc"
#endif

#ifndef MyRid
  #define MyRid "win-" + Arch
#endif

; SourceDir 的默认值按 Arch/模式推导，不再写死 win-x64（否则 -Rid win-arm64 会打包 x64 的树）。
; 完整 RID 由 build-installer.ps1 通过 /DSourceDir 传入。
#ifndef SourceDir
  #define SourceDir "..\dist\TeachCraft-win-" + Arch + "-" + MyAppMode
#endif

[Setup]
AppId={{08156D55-F74E-4B0B-A562-26D73C0E9CCD}
AppName=知微 · TeachCraft
AppVersion={#MyAppVersion}
AppVerName=知微 · TeachCraft {#MyAppVersion}
AppPublisher=Evan0148
AppPublisherURL=https://github.com/Evan0148/TeachCraft
AppSupportURL=https://github.com/Evan0148/TeachCraft/issues
AppUpdatesURL=https://github.com/Evan0148/TeachCraft/releases

DefaultDirName={autopf}\TeachCraft
DefaultGroupName=知微
AllowNoIcons=yes
DisableProgramGroupPage=yes

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif

SetupIconFile=..\src\TeachCraft.App\Assets\teachcraft.ico
UninstallDisplayIcon={app}\TeachCraft.exe
WizardStyle=modern

Compression=lzma2/max
SolidCompression=yes

OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
VersionInfoVersion={#MyAppBaseVersion}
VersionInfoDescription=知微 · TeachCraft 安装向导
VersionInfoCopyright=Copyright (C) 2026 Evan0148

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,GradeStrip.App.exe"

[Icons]
Name: "{autoprograms}\知微 · TeachCraft"; Filename: "{app}\TeachCraft.exe"
Name: "{autodesktop}\知微 · TeachCraft"; Filename: "{app}\TeachCraft.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TeachCraft.exe"; Description: "{cm:LaunchProgram,知微 · TeachCraft}"; Flags: nowait postinstall skipifsilent

; ==============================================================================
; 安装身份记录 —— 供「检查更新」自动识别安装形态与部署模式。
;
; 两个必须守住的点：
;   1. 必须写 64 位视图（HKCU64）。ArchitecturesInstallIn64BitMode 含 arm64 时，
;      Inno 的默认注册表视图是 32 位，而 .NET 读的是原生视图，写 HKCU 会让
;      x64/arm64 的键读不到。
;   2. 六个 Windows 包共用同一个 AppId，因此子键按 模式 + 架构 分开，
;      否则先装 sc 再装 fdd 会互相覆盖安装记录。
; 注意：这里刻意使用编译期常量拼接，不用 {code:...} —— Inno 文档说明
;      {code:} 在 Subkey 参数里不受支持。
; ==============================================================================
[Registry]
Root: HKCU64; Subkey: "Software\TeachCraft\Install-{#MyAppMode}-{#Arch}"; ValueType: string; ValueName: "Mode"; ValueData: "{#MyAppMode}"; Flags: uninsdeletekey
Root: HKCU64; Subkey: "Software\TeachCraft\Install-{#MyAppMode}-{#Arch}"; ValueType: string; ValueName: "Rid"; ValueData: "{#MyRid}"; Flags: uninsdeletevalue
Root: HKCU64; Subkey: "Software\TeachCraft\Install-{#MyAppMode}-{#Arch}"; ValueType: string; ValueName: "InstallRoot"; ValueData: "{app}"; Flags: uninsdeletevalue
Root: HKCU64; Subkey: "Software\TeachCraft\Install-{#MyAppMode}-{#Arch}"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletevalue
