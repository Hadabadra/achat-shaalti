; מתקין "תוכנת חסימת אוצריא - גמח תורת אסתר". בנייה:
;   ISCC.exe /DAppVersion=3.0.0 /DExePath=<נתיב ל-OtzariaBridge.exe שפורסם> OtzariaBridge.iss
; ההתקנה פר-משתמש (ללא הרשאות מנהל): הקובץ נכנס ל-%LOCALAPPDATA%\OtzariaBridge, שם הגשר מצפה להיות.
; הרכיב המוקשח (שירות SYSTEM) נשאר אופציונלי ומותקן מתוך הגדרות המערכת, כמו עד היום.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef ExePath
  #error "חובה לציין /DExePath=<נתיב ל-OtzariaBridge.exe>"
#endif

[Setup]
AppId={{6F1C2B7E-4D0A-4B8E-9C53-0E2A7B5D91A4}
AppName=תוכנת חסימת אוצריא - גמח תורת אסתר
AppVersion={#AppVersion}
AppPublisher=גמח תורת אסתר
DefaultDirName={localappdata}\OtzariaBridge
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
Compression=lzma2/max
SolidCompression=yes
OutputBaseFilename=OtzariaBridge-Setup-{#AppVersion}
OutputDir=Output
UninstallDisplayIcon={app}\OtzariaBridge.exe
UninstallDisplayName=תוכנת חסימת אוצריא - גמח תורת אסתר

WizardStyle=modern
CloseApplications=no

[Languages]
Name: "hebrew"; MessagesFile: "compiler:Languages\Hebrew.isl"

[Messages]
FinishedLabel=ההתקנה הושלמה. הגשר רץ ברקע (סמל במגש המערכת) ויעלה אוטומטית עם הכניסה ל-Windows.%n%nאם אוצריא הותקנה ופעלה בעבר, היא נפתחה כדי להתקין את התוספים "סייר קבצים" ו"הגדרות מערכת" — אשרו בחלון ההתקנה של אוצריא.%n%nאם התוספים לא הותקנו: פתחו את אוצריא פעם אחת, ואז בחרו בסמל הגשר במגש "התקן/עדכן את התוספים באוצריא".

[Files]
Source: "{#ExePath}"; DestDir: "{app}"; DestName: "OtzariaBridge.exe"; Flags: ignoreversion

[Run]
Filename: "{app}\OtzariaBridge.exe"; Parameters: "--install --quiet"; StatusMsg: "מגדיר הפעלה אוטומטית ומתקין את התוספים באוצריא..."; Flags: waituntilterminated runhidden

[Code]
// ---- אישור בפועל שהתוספים הותקנו באוצריא (לא רק שהזרימה הופעלה) ----
// אין לנו דרך תכנותית לדעת אם המשתמש אכן אישר את חלון ההתקנה באוצריא (זו זרימה
// חד-כיוונית דרך otzaria://plugin/install-local, בלי ערוץ חזרה) - לכן שואלים במפורש,
// עם אפשרות להפעיל את ההתקנה שוב ולתת את הנתיב המדויק אם עדיין לא בוצע.
var
  PluginsConfirmed: Boolean;

procedure RerunPluginInstall();
var
  rc: Integer;
begin
  Exec(ExpandConstant('{app}\OtzariaBridge.exe'), '--install --quiet', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1500);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  choice: Integer;
begin
  if CurStep <> ssPostInstall then exit;
  PluginsConfirmed := MsgBox(
    'אוצריא היה אמור להיפתח ולבקש לאשר את התקנת שני התוספים: "סייר קבצים" ו"הגדרות מערכת".' + #13#10#13#10 +
    'האם אישרת את ההתקנה של שני התוספים בחלון שנפתח באוצריא?',
    mbConfirmation, MB_YESNO) = IDYES;
  while not PluginsConfirmed do
  begin
    choice := MsgBox(
      'בלי התקנת שני התוספים באוצריא, החסימה לא תעבוד!' + #13#10#13#10 +
      'נתיב התוספים: ' + ExpandConstant('{app}') + '\plugins' + #13#10#13#10 +
      'ללחוץ "כן" כדי להפעיל שוב את ההתקנה באוצריא, או "לא" כדי להמשיך בלעדיה (לא מומלץ).',
      mbError, MB_YESNO);
    if choice = IDNO then break;
    RerunPluginInstall();
    PluginsConfirmed := MsgBox('האם אישרת עכשיו את התקנת שני התוספים באוצריא?', mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID <> wpFinished) or PluginsConfirmed then exit;
  WizardForm.FinishedLabel.Caption :=
    'התקנת רכיב החסימה הושלמה - אבל התוספים באוצריא עדיין לא אושרו!' + #13#10#13#10 +
    'יש להתקין אותם כדי שהחסימה תעבוד בפועל: פתחו את אוצריא, ואז מתפריט הגשר במגש המערכת ' +
    'בחרו "התקן/עדכן את התוספים באוצריא" - או התקינו ידנית מהנתיב:' + #13#10 +
    ExpandConstant('{app}') + '\plugins';
end;

// עוצרים גשר שרץ מהתקנה קודמת, כדי שאפשר יהיה להחליף את הקובץ
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM OtzariaBridge.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(800);
  Result := '';
end;

// ---- הסרה: מחייבת את סיסמת הנעילה, כדי שהסרה מ"אפליקציות מותקנות" לא תעקוף את הנעילה ----

function RunUninstall(const Password: String): Boolean;
var
  rc: Integer;
  exe, args: String;
begin
  exe := ExpandConstant('{app}\OtzariaBridge.exe');
  args := '--uninstall';
  if Password <> '' then
    args := args + ' --password "' + Password + '"';
  Result := Exec(exe, args, '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
end;

function AskPassword(var Password: String): Boolean;
var
  Form: TSetupForm;
  Lbl: TNewStaticText;
  Edit: TPasswordEdit;
  Ok, Cancel: TNewButton;
begin
  Form := CreateCustomForm(ScaleX(380), ScaleY(150), False, False);
  try
    Form.Caption := 'הסרת תוכנת חסימת אוצריא';
    Lbl := TNewStaticText.Create(Form);
    Lbl.Parent := Form;
    Lbl.Caption := 'הקלידו את סיסמת הנעילה (או קוד שחזור) כדי להסיר:';
    Lbl.Left := ScaleX(16);
    Lbl.Top := ScaleY(16);
    Lbl.Width := Form.ClientWidth - ScaleX(32);
    Edit := TPasswordEdit.Create(Form);
    Edit.Parent := Form;
    Edit.Left := ScaleX(16);
    Edit.Top := ScaleY(48);
    Edit.Width := Form.ClientWidth - ScaleX(32);
    Ok := TNewButton.Create(Form);
    Ok.Parent := Form;
    Ok.Caption := 'הסרה';
    Ok.ModalResult := mrOk;
    Ok.Default := True;
    Ok.Left := Form.ClientWidth - ScaleX(16) - Ok.Width;
    Ok.Top := ScaleY(100);
    Cancel := TNewButton.Create(Form);
    Cancel.Parent := Form;
    Cancel.Caption := 'ביטול';
    Cancel.ModalResult := mrCancel;
    Cancel.Cancel := True;
    Cancel.Left := Ok.Left - Cancel.Width - ScaleX(8);
    Cancel.Top := ScaleY(100);
    Form.ActiveControl := Edit;
    Result := Form.ShowModal = mrOk;
    if Result then
      Password := Edit.Text;
  finally
    Form.Free;
  end;
end;

function InitializeUninstall(): Boolean;
var
  pw: String;
  tries: Integer;
begin
  // בלי סיסמת נעילה ההסרה עוברת מיד; אחרת מבקשים סיסמה (עד 5 ניסיונות)
  Result := RunUninstall('');
  tries := 0;
  while (not Result) and (tries < 5) do
  begin
    if not AskPassword(pw) then
    begin
      Result := False;
      Exit;
    end;
    Result := RunUninstall(pw);
    if not Result then
      MsgBox('הסיסמה שגויה, או שההסרה נכשלה.', mbError, MB_OK);
    tries := tries + 1;
  end;
end;
