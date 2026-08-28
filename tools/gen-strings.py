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
