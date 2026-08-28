#!/usr/bin/env python3
"""Generates the UI resource files and the strongly-typed accessor beside them.

One source of truth for three .resx files and one C# class, so a key cannot exist in `en` and be missing
from `vi`, or exist in the resx and be missing from the class. `docs/14_I18N.md` is the spec; the parity
test in AppLedger.App.Tests is what keeps this script honest if somebody edits a resx by hand.

Usage:  python tools/gen-strings.py
"""

import io
import os

# key -> (en, vi, ja, comment or None)
# `ja` is machine-drafted and every entry carries a review marker, per docs/14 §Rules.
STRINGS = {
    "App_Title": ("AppLedger", "AppLedger", "AppLedger", None),

    "Nav_Home": ("Home", "Trang chính", "ホーム", None),
    "Nav_Apps": ("Apps", "Ứng dụng", "アプリ", None),
    "Nav_Installed": ("Installed", "Đã cài", "インストール済み", None),
    "Nav_Alerts": ("Alerts", "Cảnh báo", "アラート", None),
    "Nav_Settings": ("Settings", "Cài đặt", "設定", None),

    "Page_Home_Title": ("Home", "Trang chính", "ホーム", None),
    "Page_Apps_Title": ("Running apps", "Ứng dụng đang chạy", "実行中のアプリ", None),
    "Page_Installed_Title": ("Installed apps", "Ứng dụng đã cài", "インストール済みアプリ", None),
    "Page_Alerts_Title": ("Alerts", "Cảnh báo", "アラート", None),
    "Page_Settings_Title": ("Settings", "Cài đặt", "設定", None),

    "State_NotYetBuilt": (
        "This page arrives in a later milestone.",
        "Trang này sẽ có ở mốc sau.",
        "このページは後のマイルストーンで提供されます。",
        "Shown on pages registered for navigation but not yet implemented.",
    ),
    "State_NoDataYet": (
        "No data yet. The Agent has only just started collecting.",
        "Chưa có dữ liệu. Agent vừa bắt đầu thu thập.",
        "データはまだありません。エージェントは収集を開始したばかりです。",
        None,
    ),

    # --- Onboarding. The English is verbatim from docs/12 §Privacy Gate: it is a product decision about
    # what the user is told, not copy to be improved in passing.
    "Onboarding_Step": ("Step {0} of {1}", "Bước {0} trên {1}", "ステップ {0} / {1}",
        "{0} = current step number, {1} = total steps"),
    "Onboarding_Continue": ("Continue", "Tiếp tục", "続ける", None),
    "Onboarding_Back": ("Back", "Quay lại", "戻る", None),

    "Privacy_Gate_Title": ("Before we start", "Trước khi bắt đầu", "始める前に", None),
    "Privacy_Gate_What_Heading": ("What is recorded", "Những gì được ghi lại", "記録される内容", None),
    "Privacy_Gate_What_Body": (
        "AppLedger records which apps run, how much CPU/memory/disk/network they use, where they store "
        "files, and - for most apps - which websites/hosts they talk to.",
        "AppLedger ghi lại ứng dụng nào chạy, chúng dùng bao nhiêu CPU/bộ nhớ/đĩa/mạng, lưu tệp ở đâu, và "
        "- với hầu hết ứng dụng - chúng liên lạc với website/máy chủ nào.",
        "AppLedger は、どのアプリが実行され、CPU・メモリ・ディスク・ネットワークをどれだけ使用し、"
        "ファイルをどこに保存し、（ほとんどのアプリについては）どのウェブサイトやホストと通信したかを記録します。",
        None),
    "Privacy_Gate_Browsers_Heading": ("Web browsers", "Trình duyệt web", "ウェブ ブラウザー", None),
    "Privacy_Gate_Browsers_Body": (
        "For web browsers we record only how much data they used, not which sites. You can change this per app.",
        "Với trình duyệt web, chúng tôi chỉ ghi lượng dữ liệu đã dùng, không ghi trang nào. Bạn có thể đổi "
        "điều này cho từng ứng dụng.",
        "ウェブ ブラウザーについては、使用したデータ量のみを記録し、どのサイトかは記録しません。"
        "これはアプリごとに変更できます。",
        None),
    "Privacy_Gate_Where_Heading": ("Where it is stored", "Lưu ở đâu", "保存場所", None),
    "Privacy_Gate_Where_Body": (
        r"Everything stays on this PC in %LOCALAPPDATA%\AppLedgerData. Nothing is uploaded. AppLedger has no accounts.",
        r"Mọi thứ nằm trên máy này, trong %LOCALAPPDATA%\AppLedgerData. Không có gì được tải lên. "
        "AppLedger không có tài khoản.",
        r"すべてこの PC の %LOCALAPPDATA%\AppLedgerData に保存されます。アップロードは行われず、"
        "AppLedger にアカウントはありません。",
        None),
    "Privacy_Gate_HowLong_Heading": ("How long it is kept", "Giữ trong bao lâu", "保持期間", None),
    "Privacy_Gate_HowLong_Body": (
        "6 months by default. You can shorten it, pause, or delete everything in one click.",
        "Mặc định 6 tháng. Bạn có thể rút ngắn, tạm dừng, hoặc xoá tất cả chỉ bằng một cú nhấp.",
        "既定では 6 か月です。短縮、一時停止、またはワンクリックですべて削除できます。",
        None),
    "Privacy_Gate_Who_Heading": ("Who can see it", "Ai xem được", "閲覧できる人", None),
    "Privacy_Gate_Who_Body": (
        "Anyone who can log in as you on this PC. Protect your account accordingly.",
        "Bất kỳ ai đăng nhập được bằng tài khoản của bạn trên máy này. Hãy bảo vệ tài khoản tương xứng.",
        "この PC にあなたとしてサインインできる人は誰でも閲覧できます。アカウントを適切に保護してください。",
        None),
    "Privacy_Gate_ReadPolicy": ("Read full policy", "Đọc chính sách đầy đủ", "ポリシー全文を読む", None),

    "Agent_Setup_Title": ("The background Agent", "Agent chạy nền", "バックグラウンド エージェント", None),
    "Agent_Setup_Body": (
        "Network, disk and DNS figures need a small background service that runs with administrator rights "
        "and starts when you sign in. Installing it asks for permission once.",
        "Số liệu mạng, đĩa và DNS cần một dịch vụ nền nhỏ chạy với quyền quản trị và khởi động khi bạn đăng "
        "nhập. Cài nó sẽ hỏi quyền một lần duy nhất.",
        "ネットワーク、ディスク、DNS の数値には、管理者権限で動作しサインイン時に起動する小さな"
        "バックグラウンド サービスが必要です。インストール時に一度だけ許可を求めます。",
        None),
    "Agent_Setup_Budget": (
        "It stays under 1% CPU when idle and under 100 MB of memory.",
        "Nó giữ dưới 1% CPU khi rảnh và dưới 100 MB bộ nhớ.",
        "アイドル時は CPU 1% 未満、メモリ 100 MB 未満に収まります。",
        None),
    "Agent_Setup_Install": ("Install Agent", "Cài Agent", "エージェントをインストール", None),
    "Agent_Setup_Skip": ("Continue in Lite mode", "Tiếp tục ở chế độ rút gọn", "ライト モードで続行", None),
    "Agent_Setup_Installed": ("The Agent is installed and running.", "Agent đã cài và đang chạy.",
        "エージェントがインストールされ、実行中です。", None),
    "Agent_Setup_Declined": (
        "Continuing without the Agent. You can install it later from Settings.",
        "Tiếp tục không có Agent. Bạn có thể cài sau trong Cài đặt.",
        "エージェントなしで続行します。後で設定からインストールできます。",
        None),
    "Agent_Start": ("Start Agent", "Khởi động Agent", "エージェントを開始", None),

    "Defaults_Title": ("Defaults", "Mặc định", "既定値", None),
    "Defaults_Retention": ("Keep history for {0} days", "Giữ lịch sử {0} ngày", "履歴を {0} 日間保持",
        "{0} = number of days"),
    "Defaults_Done": ("Done", "Xong", "完了", None),

    "Col_App": ("App", "Ứng dụng", "アプリ", None),
    "Col_Procs": ("Procs", "Tiến trình", "プロセス", "Column header: live process count for the app."),
    "Col_Cpu": ("CPU %", "CPU %", "CPU %", None),
    "Col_Memory": ("Memory", "Bộ nhớ", "メモリ", "Private working set - what Task Manager calls Memory."),
    "Col_Gpu": ("GPU %", "GPU %", "GPU %", None),
    "Col_DiskRead": ("Disk R", "Đĩa đọc", "ディスク読取", None),
    "Col_DiskWrite": ("Disk W", "Đĩa ghi", "ディスク書込", None),
    "Col_NetIn": ("Net down", "Mạng xuống", "受信", None),
    "Col_NetOut": ("Net up", "Mạng lên", "送信", None),

    "Health_Mode_Connecting": ("Connecting", "Đang kết nối", "接続中", None),
    "Health_Mode_Full": ("Full", "Đầy đủ", "フル", None),
    "Health_Mode_Degraded": ("Degraded", "Suy giảm", "機能低下", None),
    "Health_Mode_Lite": ("Lite", "Rút gọn", "ライト", None),
    "Health_Cpu": ("Agent CPU", "CPU của Agent", "エージェント CPU", None),
    "Health_Memory": ("Agent memory", "Bộ nhớ Agent", "エージェント メモリ", None),
    "Health_Sensors": ("Sensors", "Cảm biến", "センサー", None),

    "Lite_Banner_Title": ("Running without the Agent", "Đang chạy không có Agent", "エージェントなしで実行中", None),
    "Lite_Banner_Body": (
        "Network, disk and DNS figures need the background Agent, which runs with administrator rights. "
        "Everything shown here comes from what a standard user can see.",
        "Số liệu mạng, đĩa và DNS cần Agent nền chạy với quyền quản trị. "
        "Mọi thứ hiển thị ở đây đến từ những gì một người dùng thường thấy được.",
        "ネットワーク、ディスク、DNS の数値には管理者権限で動作するバックグラウンド エージェントが必要です。"
        "ここに表示されているのは標準ユーザーが取得できる情報のみです。",
        None),

    "Value_NotAvailable": ("N/A", "Không có", "N/A",
        "Shown where a sensor could not run - deliberately not a zero, which would claim we looked."),

    "Menu_File": ("_File", "_Tệp", "ファイル(_F)", None),
    "Menu_File_Exit": ("E_xit", "T_hoát", "終了(_X)", None),
    "Menu_View": ("_View", "_Xem", "表示(_V)", None),
    "Menu_Help": ("_Help", "Tr_ợ giúp", "ヘルプ(_H)", None),
    "Menu_Help_About": ("_About", "_Giới thiệu", "バージョン情報(_A)", None),
}

RESX_HEADER = '''<?xml version="1.0" encoding="utf-8"?>
<root>
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" />
              </xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
'''


def escape(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def write_resx(path, index, mark_for_review):
    parts = [RESX_HEADER]
    for key, values in STRINGS.items():
        notes = [n for n in (values[3], "review" if mark_for_review else None) if n]
        parts.append(f'  <data name="{key}" xml:space="preserve">\n')
        parts.append(f"    <value>{escape(values[index])}</value>\n")
        if notes:
            parts.append(f'    <comment>{escape(" - ".join(notes))}</comment>\n')
        parts.append("  </data>\n")
    parts.append("</root>\n")
    io.open(path, "w", encoding="utf-8", newline="\n").write("".join(parts))


def write_class(path):
    lines = [
        "// <auto-generated>",
        "//     Generated by tools/gen-strings.py from Strings.resx. Do not edit by hand: edit the script,",
        "//     run it, and commit both. A parity test asserts this file and the three resx agree.",
        "// </auto-generated>",
        "",
        "using System.Globalization;",
        "using System.Resources;",
        "",
        "namespace AppLedger.App.Resources;",
        "",
        "/// <summary>Every user-visible string in the UI (docs/14_I18N.md).</summary>",
        "/// <remarks>",
        "/// <b>Public, and it has to be.</b> XAML's <c>{x:Static}</c> resolves through public reflection, so an",
        "/// internal class compiles cleanly and then throws at window construction - \"StaticExtension value",
        "/// cannot be resolved to an enumeration, static field, or static property\". Only running it says so.",
        "/// </remarks>",
        "[System.CodeDom.Compiler.GeneratedCode(\"tools/gen-strings.py\", \"1.0\")]",
        "public static class Strings",
        "{",
        "    private static readonly ResourceManager Manager =",
        "        new(\"AppLedger.App.Resources.Strings\", typeof(Strings).Assembly);",
        "",
        "    /// <summary>Looks a key up in the current UI culture.</summary>",
        "    public static string Get(string key) =>",
        "        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;",
        "",
    ]

    for key, values in STRINGS.items():
        summary = values[0].replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
        lines.append(f"    /// <summary>{summary}</summary>")
        lines.append(f"    public static string {key} => Get(nameof({key}));")
        lines.append("")

    lines.append("    /// <summary>Every key this class exposes, for the parity test.</summary>")
    lines.append("    public static IReadOnlyList<string> Keys { get; } =")
    lines.append("    [")
    for key in STRINGS:
        lines.append(f"        nameof({key}),")
    lines.append("    ];")
    lines.append("}")
    lines.append("")

    io.open(path, "w", encoding="utf-8", newline="\n").write("\n".join(lines))


def main():
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
    resources = os.path.join(root, "src", "AppLedger.App", "Resources")

    write_resx(os.path.join(resources, "Strings.resx"), 0, mark_for_review=False)
    write_resx(os.path.join(resources, "Strings.vi.resx"), 1, mark_for_review=False)
    write_resx(os.path.join(resources, "Strings.ja.resx"), 2, mark_for_review=True)
    write_class(os.path.join(resources, "Strings.cs"))

    print(f"{len(STRINGS)} keys -> 3 resx + Strings.cs")


if __name__ == "__main__":
    main()
