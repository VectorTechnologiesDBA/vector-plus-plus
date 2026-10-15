<#
.SYNOPSIS
  Builds VECTOR-wirepod.apk: the original Vector app, patched to use a wire-pod server instead of Anki's cloud.

.DESCRIPTION
  1. Extracts the app's .NET assemblies from the original APK.
  2. Compiles WirePod\*.cs into WirePod.dll against those exact (linker-trimmed) assemblies.
  3. Patches chewie.Droid.dll to call into WirePod.dll and turns off analytics in ChewieResources.dll (see README.md).
  4. Writes a new APK where every other file is copied byte-for-byte, then signs and aligns it.

  Needs the .NET 8 SDK and a JDK 11+ (javac/keytool/jarsigner). dnlib is fetched from NuGet. The extra screens need
  these in tools\ (see README.md): apktool.jar, r8-8.5.35.jar, android-4.1.1.4.jar, json-20080701.jar, jsch-0.2.20.jar.
  The original Vector app (VECTOR.apk, 1.5.0) is not included: put it next to this script or pass -Apk.
  If apksigner (Android SDK build-tools) is on PATH it is used instead of jarsigner, adding v2/v3 signatures.
#>
param(
	[string]$Apk = (Join-Path $PSScriptRoot "VECTOR.apk"),
	[string]$Out = (Join-Path $PSScriptRoot "VECTOR-wirepod.apk"),
	[string]$DnlibPath = "",
	# Bundled into the APK so first-time setup can configure the robot without asking. Never commit it or share the APK.
	[string]$SshKey = (Join-Path $PSScriptRoot "ssh_root_key"),
	[string]$Tools = (Join-Path $PSScriptRoot "tools"),
	[string]$Keystore = (Join-Path $PSScriptRoot "wirepod-debug.keystore"),
	[string]$KeystorePassword = "android",
	[string]$KeyAlias = "wirepod"
)

$ErrorActionPreference = "Stop"

function Invoke-Native {
	param([string]$Exe, [string[]]$Arguments)
	& $Exe @Arguments
	if ($LASTEXITCODE -ne 0) { throw "$Exe failed with exit code $LASTEXITCODE" }
}

function Find-Csc {
	$sdk = & dotnet --list-sdks | Select-Object -Last 1
	if (-not $sdk) { throw "No .NET SDK found (dotnet --list-sdks)" }
	# "8.0.425 [C:\Program Files\dotnet\sdk]"
	$version, $root = $sdk -split " \[", 2
	$csc = Join-Path $root.TrimEnd("]") "$version\Roslyn\bincore\csc.dll"
	if (-not (Test-Path $csc)) { throw "csc.dll not found at $csc" }
	return $csc
}

if (-not (Test-Path $Apk)) { throw "Original Vector app not found at $Apk. Copy VECTOR.apk (version 1.5.0) next to build.ps1 or pass -Apk <path>." }
$Apk = (Resolve-Path $Apk).Path
if ($DnlibPath -and -not (Test-Path $DnlibPath)) { throw "dnlib.dll not found at $DnlibPath" }

$build = Join-Path $PSScriptRoot "build"
# Antivirus scans of freshly written DLLs can briefly lock them, so retry before giving up.
for ($attempt = 1; Test-Path $build; $attempt++) {
	try { Remove-Item -Recurse -Force $build -ErrorAction Stop }
	catch {
		if ($attempt -ge 5) { throw "Can't clear $build ($($_.Exception.Message)). Close any program that has a file from it open (for example dnSpy) and run the build again." }
		Start-Sleep -Seconds 2
	}
}
New-Item -ItemType Directory -Force $build | Out-Null
$ref = Join-Path $build "ref"
$patched = Join-Path $build "patched"

Write-Host "== Building Patcher"
$patcherArgs = @("build", (Join-Path $PSScriptRoot "Patcher\Patcher.csproj"), "-c", "Release", "-nologo", "-v", "q")
if ($DnlibPath) { $patcherArgs += "-p:DnlibPath=$DnlibPath" }
Invoke-Native dotnet $patcherArgs
$patcher = Join-Path $PSScriptRoot "Patcher\bin\Release\net8.0\Patcher.dll"

Write-Host "== Extracting assemblies from $Apk"
Invoke-Native dotnet @($patcher, "extract", $Apk, $ref)

Write-Host "== Compiling WirePod.dll against the app's assemblies"
$cscArgs = @("-nologo", "-noconfig", "-nostdlib+", "-target:library", "-optimize+", "-deterministic", "-langversion:7.3", "-out:$(Join-Path $build 'WirePod.dll')")
$cscArgs += Get-ChildItem (Join-Path $ref "*.dll") | ForEach-Object { "-r:$($_.FullName)" }
$cscArgs += Get-ChildItem (Join-Path $PSScriptRoot "WirePod\*.cs") | ForEach-Object { $_.FullName }
Invoke-Native dotnet (@((Find-Csc)) + $cscArgs)

Write-Host "== Patching chewie.Droid.dll and ChewieResources.dll"
Invoke-Native dotnet @($patcher, "patch", $ref, (Join-Path $build "WirePod.dll"), $patched)

Write-Host "== Fixing fonts (removing AAT tables that newer Android renders oddly)"
$fonts = Join-Path $build "fonts"
Invoke-Native dotnet @($patcher, "fonts", $Apk, $fonts)

Write-Host "== Compiling the extra screens (Java) into classes3.dex"
$java = Join-Path $build "java"
# The Android 15 API (it includes org.json) so screens can use newer APIs such as PackageInstaller.
$cp = @("android-35\android.jar", "json-20080701.jar", "jsch-0.2.20.jar") | ForEach-Object { Join-Path $Tools $_ }
Invoke-Native javac (@("--release", "8", "-nowarn", "-encoding", "UTF-8", "-cp", ($cp -join ";"), "-d", (Join-Path $java "cls")) +
	(Get-ChildItem -Recurse (Join-Path $PSScriptRoot "android\src") -Filter *.java | ForEach-Object { $_.FullName }))
New-Item -ItemType Directory -Force (Join-Path $java "dex") | Out-Null
Invoke-Native java (@("-cp", (Join-Path $Tools "r8-8.5.35.jar"), "com.android.tools.r8.D8", "--release", "--min-api", "26",
	"--lib", $cp[0], "--lib", $cp[1], "--output", (Join-Path $java "dex"), $cp[2]) +
	(Get-ChildItem -Recurse (Join-Path $java "cls") -Filter *.class | ForEach-Object { $_.FullName }))

Write-Host "== Rebuilding manifest, resources and dex (new screens and icons, Android 15 target; resource IDs are kept)"
$res = Join-Path $build "res"
$apktool = Join-Path $Tools "apktool.jar"
Invoke-Native java @("-jar", $apktool, "d", "-f", "-o", (Join-Path $res "src"), $Apk)
Copy-Item (Join-Path $PSScriptRoot "android\res\drawable\*.xml") (Join-Path $res "src\res\drawable")
$manifest = Join-Path $res "src\AndroidManifest.xml"
$activities = "ExtrasActivity", "WeatherActivity", "KnowledgeActivity", "TalkActivity", "DriveActivity", "SshActivity", "EyeColorActivity", "UpdateActivity" | ForEach-Object {
	"        <activity android:name=`"com.wirepod.extras.$_`" android:theme=`"@android:style/Theme.Material.NoActionBar`" android:screenOrientation=`"$(if ($_ -eq "DriveActivity" -or $_ -eq "SshActivity") { "sensorLandscape" } else { "portrait" })`"$(if ($_ -eq "UpdateActivity") { ' android:launchMode="singleTop"' })/>" }
(Get-Content -Raw $manifest).Replace("</application>", (($activities -join "`n") + "`n    </application>")) | Set-Content -NoNewline -Encoding utf8 $manifest
# Target Android 15 (API 35) so newer Android stops warning that the app is for an old Android version. That needs:
#  - android:exported on components with intent filters (Android 12);
#  - the Android 12 Bluetooth permissions (requested at runtime by WirePod.Permissions);
#  - PendingIntents that say whether they're mutable (Android 12): every PendingIntent.get* call in the app's own
#    dex files (Appboy, Firebase, support library) goes through com.wirepod.extras.Compat, which adds FLAG_IMMUTABLE;
#  - receivers registered at runtime that say whether they're exported (Android 14): same idea, Compat.registerReceiver;
#  - allowing plain HTTP (Android 9), which wire-pod uses;
#  - turning off heap pointer tagging (Android 11), which the old Mono runtime crashes on;
#  - opting out of Android 15's forced edge-to-edge layout, which this app's screens weren't designed for.
$yml = Join-Path $res "src\apktool.yml"
(Get-Content -Raw $yml) -replace "targetSdkVersion: '?\d+'?", "targetSdkVersion: '35'" | Set-Content -NoNewline -Encoding utf8 $yml
$text = Get-Content -Raw $manifest
$text = $text.Replace('<activity android:icon="@mipmap/ic_launcher" android:name=', '<activity android:exported="true" android:icon="@mipmap/ic_launcher" android:name=')
# Android 11+ tags heap pointers (top byte) for apps targeting it; the app's 2019 Mono runtime can't cope and aborts
# at startup (mini-arm64.c assertion), so opt out.
# wire-pod (and the firmware download) are plain HTTP, which Android 9+ blocks by default for apps targeting it.
$text = $text.Replace('<application android:allowBackup="true"', '<application android:allowNativeHeapPointerTagging="false" android:usesCleartextTraffic="true" android:allowBackup="true"')
$text = $text.Replace('<receiver android:name="com.appboy.AppboyFcmReceiver"', '<receiver android:exported="true" android:name="com.appboy.AppboyFcmReceiver"')
$text = $text.Replace('<uses-permission android:name="android.permission.BLUETOOTH"/>', ('<uses-permission android:name="android.permission.BLUETOOTH"/>' +
	"`n    <uses-permission android:name=`"android.permission.BLUETOOTH_SCAN`"/>" +
	"`n    <uses-permission android:name=`"android.permission.BLUETOOTH_CONNECT`"/>" +
	"`n    <uses-permission android:name=`"android.permission.ACCESS_FINE_LOCATION`"/>" +
	"`n    <uses-permission android:name=`"android.permission.POST_NOTIFICATIONS`"/>" +
	"`n    <uses-permission android:name=`"android.permission.REQUEST_INSTALL_PACKAGES`"/>" +
	"`n    <queries><intent><action android:name=`"android.intent.action.VIEW`"/><data android:scheme=`"https`"/></intent>" +
	"<intent><action android:name=`"android.intent.action.SENDTO`"/><data android:scheme=`"mailto`"/></intent></queries>"))
Set-Content -NoNewline -Encoding utf8 $manifest $text
$rewired = 0
foreach ($smali in Get-ChildItem -Recurse (Join-Path $res "src") -Filter *.smali) {
	$code = [IO.File]::ReadAllText($smali.FullName)
	$new = [regex]::Replace($code, "Landroid/app/PendingIntent;->(getActivity|getActivities|getBroadcast|getService)\(", "Lcom/wirepod/extras/Compat;->`$1(")
	# invoke-virtual {ctx, receiver, filter[, permission, handler]}, Context->registerReceiver(...) becomes a static call
	# with the same registers, the Context passed first.
	$new = [regex]::Replace($new, "invoke-virtual(/range)? (\{[^}]*\}), Landroid/content/Context;->registerReceiver\(",
		"invoke-static`$1 `$2, Lcom/wirepod/extras/Compat;->registerReceiver(Landroid/content/Context;")
	if ($new -ne $code) { [IO.File]::WriteAllText($smali.FullName, $new); $rewired++ }
}
Write-Host "   PendingIntent/registerReceiver calls rewired in $rewired classes"
$styles = Join-Path $res "src\res\values\styles.xml"
$optOut = "`n        <item name=`"android:windowOptOutEdgeToEdgeEnforcement`">true</item>"
$xml = [regex]::Replace((Get-Content -Raw $styles), '(<style name="Chewie[^"]*"[^>]*?)\s*/>', "`$1>$optOut`n    </style>")
$xml = [regex]::Replace($xml, '(<style name="Chewie[^"]*"[^>]*[^/]>)(?!\s*<item name="android:windowOptOut)', "`$1$optOut")
Set-Content -NoNewline -Encoding utf8 $styles $xml
# Build against the Android 15 framework (tools\android-35\android.jar) so the API 35 theme attribute resolves.
$framework = Join-Path $Tools "apktool-framework"
if (-not (Test-Path (Join-Path $framework "1.apk"))) {
	Invoke-Native java @("-jar", $apktool, "if", (Join-Path $Tools "android-35\android.jar"), "-p", $framework)
}
Invoke-Native java @("-jar", $apktool, "b", "-p", $framework, "-o", (Join-Path $res "res.zip"), (Join-Path $res "src"))
$resOut = Join-Path $res "out"
Expand-Archive (Join-Path $res "res.zip") (Join-Path $res "all")
New-Item -ItemType Directory -Force $resOut | Out-Null
Move-Item (Join-Path $res "all\AndroidManifest.xml"), (Join-Path $res "all\resources.arsc"), (Join-Path $res "all\res"),
	(Join-Path $res "all\classes.dex"), (Join-Path $res "all\classes2.dex") $resOut

Write-Host "== Packing APK"
$unsigned = Join-Path $build "unsigned.apk"
$replaced = @(
	"assemblies/chewie.Droid.dll=$(Join-Path $patched 'chewie.Droid.dll')",
	"assemblies/ChewieResources.dll=$(Join-Path $patched 'ChewieResources.dll')",
	"assemblies/WirePod.dll=$(Join-Path $patched 'WirePod.dll')",
	"assemblies/MvvmCross.Binding.Droid.dll=$(Join-Path $patched 'MvvmCross.Binding.Droid.dll')")
$replaced += "@$resOut"
# After the rebuilt res/ so the fixed fonts win.
$replaced += Get-ChildItem (Join-Path $fonts "*.otf") | ForEach-Object { "res/font/$($_.Name)=$($_.FullName)" }
$replaced += "classes3.dex=$(Join-Path $java 'dex\classes.dex')"
if (Test-Path $SshKey) {
	Write-Host "   Bundling SSH key $SshKey (keep this APK private)"
	$replaced += "assets/wirepod/ssh_root_key=$((Resolve-Path $SshKey).Path)"
}
else { Write-Host "   No SSH key at $SshKey; setup will ask for the key file instead" }
Invoke-Native dotnet (@($patcher, "pack", $Apk, $unsigned) + $replaced)

if (-not (Test-Path $Keystore)) {
	Write-Host "== Creating signing key $Keystore"
	Invoke-Native keytool @("-genkeypair", "-keystore", $Keystore, "-storepass", $KeystorePassword, "-keypass", $KeystorePassword,
		"-alias", $KeyAlias, "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000", "-dname", "CN=Vector wire-pod build")
}

# Android 11+ only installs apps targeting it (this build targets 15) with a v2+ signature, so apksigner is required:
# tools\apksigner.jar (from Android build-tools, see README.md) or apksigner on PATH. It signs the whole file, so align first.
$apksignerJar = Join-Path $Tools "apksigner.jar"
$apksigner = Get-Command apksigner -ErrorAction SilentlyContinue
if (Test-Path $apksignerJar) { $signer = @("java", "-jar", $apksignerJar) }
elseif ($apksigner) { $signer = @($apksigner.Source) }
else { throw "apksigner not found. Put apksigner.jar in $Tools (see README.md) or add Android build-tools to PATH." }
Write-Host "== Aligning and signing with apksigner (v1 + v2 + v3)"
Invoke-Native dotnet @($patcher, "align", $unsigned, $Out)
Invoke-Native $signer[0] (@($signer | Select-Object -Skip 1) + @("sign", "--ks", $Keystore, "--ks-pass", "pass:$KeystorePassword", "--ks-key-alias", $KeyAlias, $Out))
Invoke-Native $signer[0] (@($signer | Select-Object -Skip 1) + @("verify", $Out))

Write-Host "== Checking the result"
Invoke-Native dotnet (@($patcher, "verify", $Apk, $Out) + $replaced)
Write-Host "Built $Out"
