# ============================================================================
#  WhSnap - Focal - verify-theme.ps1
#  Proves theme/Theme.xaml is a loadable WPF ResourceDictionary and that every
#  documented resource key exists (StaticResource chains inside the dictionary
#  are resolved by XamlReader, so a broken key reference fails here too).
#  Usage:  powershell -File verify-theme.ps1
#  NOTE: ASCII-only on purpose - Windows PowerShell 5.1 reads .ps1 as ANSI.
# ============================================================================
$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$theme   = Join-Path $root 'Theme.xaml'
if (-not (Test-Path $theme)) { throw "Theme.xaml not found next to this script" }

$work = Join-Path $env:TEMP 'focal-theme-verify'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null

$proj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>disable</Nullable>
    <AssemblyName>ThemeCheck</AssemblyName>
    <RootNamespace>ThemeCheck</RootNamespace>
  </PropertyGroup>
</Project>
'@
Set-Content -Path (Join-Path $work 'ThemeCheck.csproj') -Value $proj -Encoding UTF8

$program = @'
using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;

class Program
{
    static readonly string[] Keys = {
        "Focal.VoidColor", "Focal.Obsidian900Color", "Focal.Obsidian800Color",
        "Focal.Obsidian700Color", "Focal.Obsidian600Color",
        "Focal.PaperColor", "Focal.LinenColor", "Focal.LinenDeepColor",
        "Focal.InkColor", "Focal.InkMutedColor", "Focal.InkFaintColor",
        "Focal.OnDarkColor", "Focal.OnDarkMutedColor", "Focal.OnDarkFaintColor",
        "Focal.AccentColor", "Focal.AccentHoverColor", "Focal.AccentPressColor",
        "Focal.AccentDeepColor", "Focal.AccentInkColor",
        "Focal.SignalOkColor", "Focal.SignalWarnColor", "Focal.SignalDangerColor",
        "Focal.MeasureElementColor", "Focal.MeasureCenterColor",
        "Focal.Obsidian900Brush", "Focal.Obsidian800Brush", "Focal.Obsidian700Brush",
        "Focal.Obsidian600Brush", "Focal.PaperBrush", "Focal.LinenBrush",
        "Focal.InkBrush", "Focal.InkMutedBrush", "Focal.OnDarkBrush",
        "Focal.OnDarkMutedBrush", "Focal.OnDarkFaintBrush",
        "Focal.AccentBrush", "Focal.AccentHoverBrush", "Focal.AccentPressBrush",
        "Focal.AccentDeepBrush", "Focal.AccentInkBrush",
        "Focal.HairlineDarkBrush", "Focal.HairlineDarkStrongBrush", "Focal.HairlineLightBrush",
        "Focal.AccentTintBrush", "Focal.ScrimBrush",
        "Focal.DisplayFont", "Focal.TextFont", "Focal.DataFont",
        "Focal.DisplaySize", "Focal.TitleSize", "Focal.SubtitleSize", "Focal.BodySize",
        "Focal.CaptionSize", "Focal.MicroSize", "Focal.DataSize", "Focal.DataLargeSize",
        "Focal.RadiusHair", "Focal.RadiusControl", "Focal.RadiusCard", "Focal.RadiusPanel",
        "Focal.ControlHeight", "Focal.RowHeight", "Focal.ToolbarHeight", "Focal.TitleBarHeight",
        "Focal.RowPadding", "Focal.PanelPadding",
        "Focal.ShadowChrome", "Focal.ShadowDialog", "Focal.ShadowProduct",
        "Focal.DisplayText", "Focal.TitleText", "Focal.BodyText", "Focal.CaptionText",
        "Focal.MicroText", "Focal.DataText",
        "Focal.Button.Primary", "Focal.Button.Secondary", "Focal.Button.Quiet",
        "Focal.Button.Danger", "Focal.Button.Tool", "Focal.Switch", "Focal.Kbd",
        "Focal.ChromeSurface"
    };

    static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "Theme.xaml";
        ResourceDictionary dict;
        try
        {
            using (var fs = File.OpenRead(path))
            {
                dict = (ResourceDictionary)XamlReader.Load(fs);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("LOAD FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }

        Console.WriteLine("dictionary loaded, entries = " + dict.Count);
        int missing = 0;
        foreach (var k in Keys)
        {
            if (!dict.Contains(k)) { Console.WriteLine("MISSING KEY: " + k); missing++; }
        }
        Console.WriteLine("checked " + Keys.Length + " keys, missing = " + missing);
        if (missing > 0) return 1;
        Console.WriteLine("Focal theme: all keys resolve, dictionary is loadable.");
        return 0;
    }
}
'@
Set-Content -Path (Join-Path $work 'Program.cs') -Value $program -Encoding UTF8

Push-Location $work
try {
  $out = & dotnet run --project (Join-Path $work 'ThemeCheck.csproj') -- $theme 2>&1
  $code = $LASTEXITCODE
  $out | ForEach-Object { Write-Host $_ }
  Write-Host "exit=$code"
} finally {
  Pop-Location
}
