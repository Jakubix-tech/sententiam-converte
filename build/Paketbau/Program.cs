// Paketbau für Sententiam converte.
//
//   dotnet run --project build/Paketbau                        alle Pakete
//   dotnet run --project build/Paketbau -- win-x64 android     nur bestimmte
//
// Ziele: win-x64, linux-x64, osx-arm64, osx-x64, android, server, erweiterung, sdk
// Ergebnis: artifacts/<Version>/ mit den Paketen und SHA256SUMS.txt.
// Linux- und macOS-Pakete sind .tar.gz, damit die Ausführungsrechte erhalten bleiben (auch wenn unter Windows gebaut).

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

var root = FindRoot();
var version = Regex.Match(File.ReadAllText(Path.Combine(root, "Directory.Build.props")), "<Version>([^<]+)</Version>").Groups[1].Value;
var output = Path.Combine(root, "artifacts", version);
var work = Path.Combine(root, "artifacts", "arbeit");
string[] all = ["win-x64", "linux-x64", "osx-arm64", "osx-x64", "android", "server", "erweiterung", "sdk"];
var targets = args.Length == 0 ? all : args;
foreach (var t in targets.Where(t => !all.Contains(t))) { Console.Error.WriteLine($"Unbekanntes Ziel: {t}. Möglich: {string.Join(", ", all)}"); return 1; }

Directory.CreateDirectory(output);
Console.WriteLine($"Sententiam converte {version} → {output}");
var made = new List<string>();
foreach (var target in targets)
{
    var sw = Stopwatch.StartNew();
    Console.WriteLine($"\n=== {target} ===");
    var file = target switch
    {
        "win-x64" => Windows(),
        "linux-x64" => Linux(),
        "osx-arm64" or "osx-x64" => Mac(target),
        "android" => Android(),
        "server" => Server(),
        "sdk" => Sdk(),
        _ => ExampleExtension(),
    };
    made.Add(file);
    Console.WriteLine($"→ {Path.GetFileName(file)} ({new FileInfo(file).Length / 1024.0 / 1024.0:0.0} MB, {sw.Elapsed.TotalSeconds:0} s)");
}

var sums = Directory.GetFiles(output).Where(f => !f.EndsWith("SHA256SUMS.txt")).OrderBy(f => f)
    .Select(f => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant()}  {Path.GetFileName(f)}");
File.WriteAllLines(Path.Combine(output, "SHA256SUMS.txt"), sums);
Console.WriteLine($"\nFertig: {made.Count} Paket(e) in {output}");
return 0;

// ---------- Ziele ----------

string Windows()
{
    var app = Publish("SententiamConverte.Desktop", "win-x64");
    AddDocs(app);
    File.WriteAllText(Path.Combine(app, "LIESMICH.txt"), ReadMe("Windows",
        "Entpacken und „SententiamConverte.Desktop.exe“ starten. Keine Installation nötig; Einstellungen liegen unter %LOCALAPPDATA%\\SententiamConverte.\n" +
        "Windows SmartScreen kann beim ersten Start warnen, weil das Programm nicht signiert ist: „Weitere Informationen“ → „Trotzdem ausführen“."),
        new UTF8Encoding(true));
    return Zip(app, $"SententiamConverte-{version}-win-x64.zip", "Sententiam converte");
}

string Linux()
{
    var app = Publish("SententiamConverte.Desktop", "linux-x64");
    AddDocs(app);
    File.Copy(Path.Combine(root, "build", "sententiam-converte.png"), Path.Combine(app, "sententiam-converte.png"), true);
    File.WriteAllText(Path.Combine(app, "installieren.sh"), LinuxInstaller.Replace("\r\n", "\n"));
    File.WriteAllText(Path.Combine(app, "LIESMICH.txt"), ReadMe("Linux",
        "Entpacken und „./SententiamConverte.Desktop“ starten – oder „./installieren.sh“ ausführen: das richtet das Programm für den\n" +
        "aktuellen Benutzer unter ~/.local/opt ein und legt einen Eintrag im Anwendungsmenü an (keine Root-Rechte nötig).\n" +
        "Benötigt: eine grafische Oberfläche (X11 oder Wayland mit XWayland), fontconfig und libICU."));
    return TarGz(app, $"SententiamConverte-{version}-linux-x64.tar.gz", "sententiam-converte",
        p => p is "SententiamConverte.Desktop" or "createdump" or "installieren.sh");
}

string Mac(string rid)
{
    var app = Publish("SententiamConverte.Desktop", rid);
    var bundle = Path.Combine(work, rid + "-bundle", "Sententiam converte.app");
    if (Directory.Exists(Path.GetDirectoryName(bundle))) Directory.Delete(Path.GetDirectoryName(bundle)!, true);
    var contents = Path.Combine(bundle, "Contents");
    CopyDir(app, Path.Combine(contents, "MacOS"));
    Directory.CreateDirectory(Path.Combine(contents, "Resources"));
    File.Copy(Path.Combine(root, "build", "AppIcon.icns"), Path.Combine(contents, "Resources", "AppIcon.icns"));
    File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist(version));
    var outer = Path.GetDirectoryName(bundle)!;
    AddDocs(outer);
    File.WriteAllText(Path.Combine(outer, "LIESMICH.txt"), ReadMe("macOS",
        "„Sententiam converte.app“ in den Ordner „Programme“ ziehen. Das Programm ist nicht von Apple beglaubigt: beim ersten Start\n" +
        "mit Rechtsklick → „Öffnen“ bestätigen. Meldet macOS, die App sei „beschädigt“, im Terminal einmal ausführen:\n" +
        "  xattr -cr \"/Applications/Sententiam converte.app\"\n" +
        (rid == "osx-arm64" ? "Dieses Paket ist für Macs mit Apple-Chip (M1 und neuer)." : "Dieses Paket ist für Macs mit Intel-Prozessor.")));
    return TarGz(outer, $"SententiamConverte-{version}-{rid}.tar.gz", null,
        p => p.EndsWith("/MacOS/SententiamConverte.Desktop") || p.EndsWith("/MacOS/createdump"));
}

string Android()
{
    var stage = Path.Combine(work, "android");
    // Alter Release-Zwischenstand kann zu einem APK führen, das erst auf dem Gerät abstürzt – immer frisch bauen
    var obj = Path.Combine(root, "SententiamConverte.Android", "obj", "Release");
    if (Directory.Exists(obj)) Directory.Delete(obj, true);
    Run("dotnet", ["publish", Path.Combine(root, "SententiamConverte.Android", "SententiamConverte.Android.csproj"),
        "-c", "Release", "-f", "net10.0-android", "-o", stage, "-p:AndroidPackageFormat=apk"]);
    var apk = Directory.GetFiles(stage, "*-Signed.apk").FirstOrDefault() ?? Directory.GetFiles(stage, "*.apk").First();
    var target = Path.Combine(output, $"SententiamConverte-{version}-android.apk");
    File.Copy(apk, target, true);
    return target;
}

string Server()
{
    var stage = Path.Combine(work, "server");
    if (Directory.Exists(stage)) Directory.Delete(stage, true);
    Run("dotnet", ["publish", Path.Combine(root, "SententiamConverte.Server", "SententiamConverte.Server.csproj"),
        "-c", "Release", "-o", stage, "-p:DebugType=none", "-p:DebugSymbols=false"]);
    AddDocs(stage);
    File.Copy(Path.Combine(root, "SententiamConverte.Server", "Dockerfile"), Path.Combine(stage, "Dockerfile.beispiel"), true);
    File.WriteAllText(Path.Combine(stage, "LIESMICH.txt"), ReadMe("Teamserver",
        "Benötigt die ASP.NET Core Runtime 10 (https://dotnet.microsoft.com/download) – oder als Container (siehe Dockerfile.beispiel).\n" +
        "Start:      dotnet SententiamConverte.Server.dll --daten ./teamdaten\n" +
        "Benutzer:   dotnet SententiamConverte.Server.dll --daten ./teamdaten benutzer neu Anna bearbeiter\n" +
        "Ressource:  dotnet SententiamConverte.Server.dll --daten ./teamdaten ressource neu tm Maschinenbau\n" +
        "Port 5280; REST-API unter /api/v2, Beschreibung unter /openapi/v2.json. Im Internet nur hinter HTTPS betreiben."));
    return Zip(stage, $"SententiamConverte-Server-{version}.zip", "sententiam-server");
}

/// <summary>NuGet-Pakete für Entwickler: SententiamConverte.Core (Erweiterungen, ISententiam) und SententiamConverte.Sdk (REST-Client).</summary>
string Sdk()
{
    foreach (var project in new[] { "SententiamConverte.Core", "SententiamConverte.Sdk" })
        Run("dotnet", ["pack", Path.Combine(root, project, project + ".csproj"), "-c", "Release", "-o", output, "-p:IncludeSymbols=false"]);
    return Path.Combine(output, $"SententiamConverte.Sdk.{version}.nupkg");
}

string ExampleExtension()
{
    Run("dotnet", ["build", Path.Combine(root, "Erweiterungen", "Beispiel.Erweiterung", "Beispiel.Erweiterung.csproj"), "-c", "Release"]);
    var scext = Path.Combine(root, "Erweiterungen", "Beispiel.Erweiterung", "bin", "Release", "net10.0", "Beispiel.Erweiterung.scext");
    var target = Path.Combine(output, $"Beispiel.Erweiterung-{version}.scext");
    File.Copy(scext, target, true);
    return target;
}

// ---------- Hilfen ----------

string Publish(string project, string rid)
{
    var stage = Path.Combine(work, rid);
    if (Directory.Exists(stage)) Directory.Delete(stage, true);
    Run("dotnet", ["publish", Path.Combine(root, project, project + ".csproj"), "-c", "Release", "-r", rid, "--self-contained", "true",
        "-o", stage, "-p:DebugType=none", "-p:DebugSymbols=false", "-p:PublishReadyToRun=false"]);
    return stage;
}

void AddDocs(string folder)
{
    File.Copy(Path.Combine(root, "THIRD-PARTY-NOTICES.md"), Path.Combine(folder, "THIRD-PARTY-NOTICES.md"), true);
    var docs = Path.Combine(folder, "Dokumentation");
    Directory.CreateDirectory(docs);
    foreach (var f in Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
    {
        var target = Path.Combine(docs, Path.GetRelativePath(Path.Combine(root, "docs"), f));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(f, target, true);
    }
}

string ReadMe(string platform, string howTo) =>
    $"Sententiam converte {version} – {platform}\n\n{howTo}\n\n" +
    "Handbuch: im Programm unter „Hilfe“ (F1) und im Ordner „Dokumentation“.\n" +
    "Lizenzen der verwendeten Bibliotheken: THIRD-PARTY-NOTICES.md\n";

string Zip(string folder, string name, string top)
{
    var target = Path.Combine(output, name);
    if (File.Exists(target)) File.Delete(target);
    using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
    foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        zip.CreateEntryFromFile(f, top + "/" + Path.GetRelativePath(folder, f).Replace('\\', '/'), CompressionLevel.Optimal);
    return target;
}

/// <summary>tar.gz mit Unix-Rechten; <paramref name="top"/> null = Inhalt des Ordners direkt (z. B. die .app).</summary>
string TarGz(string folder, string name, string? top, Func<string, bool> executable)
{
    var target = Path.Combine(output, name);
    using var fs = File.Create(target);
    using var gz = new GZipStream(fs, CompressionLevel.Optimal);
    using var tar = new TarWriter(gz, TarEntryFormat.Pax);
    const UnixFileMode dirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                                 UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    string Entry(string path) => (top == null ? "" : top + "/") + Path.GetRelativePath(folder, path).Replace('\\', '/');
    if (top != null) tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/") { Mode = dirMode });
    foreach (var d in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).OrderBy(d => d))
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, Entry(d) + "/") { Mode = dirMode });
    foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f))
    {
        var entryName = Entry(f);
        var rel = Path.GetRelativePath(folder, f).Replace('\\', '/');
        using var data = File.OpenRead(f);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            Mode = executable(rel) || executable("/" + entryName) ? dirMode : fileMode,
            DataStream = data,
            ModificationTime = File.GetLastWriteTimeUtc(f),
        });
    }
    return target;
}

void CopyDir(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
    foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), true);
}

void Run(string exe, string[] arguments)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in arguments) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var log = new StringBuilder();
    p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        Console.Error.WriteLine(log.ToString());
        throw new InvalidOperationException($"{exe} {string.Join(' ', arguments)} ist fehlgeschlagen ({p.ExitCode}).");
    }
}

static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "SententiamConverte.slnx"))) d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}

static string InfoPlist(string version) => $"""
    <?xml version="1.0" encoding="UTF-8"?>
    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
    <plist version="1.0">
    <dict>
      <key>CFBundleName</key><string>Sententiam converte</string>
      <key>CFBundleDisplayName</key><string>Sententiam converte</string>
      <key>CFBundleIdentifier</key><string>net.jakubix.sententiamconverte</string>
      <key>CFBundleVersion</key><string>{version}</string>
      <key>CFBundleShortVersionString</key><string>{version}</string>
      <key>CFBundlePackageType</key><string>APPL</string>
      <key>CFBundleExecutable</key><string>SententiamConverte.Desktop</string>
      <key>CFBundleIconFile</key><string>AppIcon</string>
      <key>LSMinimumSystemVersion</key><string>12.0</string>
      <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
      <key>NSHighResolutionCapable</key><true/>
    </dict>
    </plist>
    """;

partial class Program
{
    const string LinuxInstaller = """
        #!/bin/sh
        # Richtet Sententiam converte für den aktuellen Benutzer ein (keine Root-Rechte nötig).
        set -e
        QUELLE="$(cd "$(dirname "$0")" && pwd)"
        ZIEL="$HOME/.local/opt/sententiam-converte"
        mkdir -p "$ZIEL" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/256x256/apps"
        cp -r "$QUELLE/." "$ZIEL/"
        chmod +x "$ZIEL/SententiamConverte.Desktop"
        cp "$ZIEL/sententiam-converte.png" "$HOME/.local/share/icons/hicolor/256x256/apps/sententiam-converte.png"
        cat > "$HOME/.local/share/applications/sententiam-converte.desktop" <<EOF
        [Desktop Entry]
        Type=Application
        Name=Sententiam converte
        Comment=Übersetzungsumgebung mit Translation Memory, Terminologie und KI-Agenten
        Exec="$ZIEL/SententiamConverte.Desktop" %F
        Icon=sententiam-converte
        Categories=Office;Translation;
        Terminal=false
        EOF
        echo "Eingerichtet: $ZIEL"
        echo "Start über das Anwendungsmenü oder mit: \"$ZIEL/SententiamConverte.Desktop\""
        """;
}
