<#
.SYNOPSIS
  Builds VECTOR-wirepod.apk: the original Vector app, patched to use a wire-pod server instead of Anki's cloud.

.DESCRIPTION
  1. Extracts the app's .NET assemblies from the original APK.
  2. Compiles WirePod\*.cs into WirePod.dll against those exact (linker-trimmed) assemblies.
  3. Patches chewie.Droid.dll to call into WirePod.dll and turns off analytics in ChewieResources.dll (see README.md).
  4. Writes a new APK where every other file is copied byte-for-byte, then signs and aligns it.

  Needs the .NET 8 SDK and a JDK (keytool/jarsigner). dnlib is fetched from NuGet.
  The original Vector app (VECTOR.apk, 1.5.0) is not included: put it next to this script or pass -Apk.
  If apksigner (Android SDK build-tools) is on PATH it is used instead of jarsigner, adding v2/v3 signatures.
#>
param(
	[string]$Apk = (Join-Path $PSScriptRoot "VECTOR.apk"),
	[string]$Out = (Join-Path $PSScriptRoot "VECTOR-wirepod.apk"),
	[string]$DnlibPath = "",
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

Write-Host "== Packing APK"
$unsigned = Join-Path $build "unsigned.apk"
Invoke-Native dotnet @($patcher, "pack", $Apk, $unsigned,
	"assemblies/chewie.Droid.dll=$(Join-Path $patched 'chewie.Droid.dll')",
	"assemblies/ChewieResources.dll=$(Join-Path $patched 'ChewieResources.dll')",
	"assemblies/WirePod.dll=$(Join-Path $patched 'WirePod.dll')")

if (-not (Test-Path $Keystore)) {
	Write-Host "== Creating signing key $Keystore"
	Invoke-Native keytool @("-genkeypair", "-keystore", $Keystore, "-storepass", $KeystorePassword, "-keypass", $KeystorePassword,
		"-alias", $KeyAlias, "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000", "-dname", "CN=Vector wire-pod build")
}

$apksigner = Get-Command apksigner -ErrorAction SilentlyContinue
if ($apksigner) {
	# apksigner signs the whole file, so align first.
	Write-Host "== Aligning and signing with apksigner"
	Invoke-Native dotnet @($patcher, "align", $unsigned, $Out)
	Invoke-Native $apksigner.Source @("sign", "--ks", $Keystore, "--ks-pass", "pass:$KeystorePassword", "--ks-key-alias", $KeyAlias, $Out)
	Invoke-Native $apksigner.Source @("verify", $Out)
}
else {
	# jarsigner (v1 signature, fine for this app's targetSdk 27) rewrites the zip, so align afterwards.
	Write-Host "== Signing with jarsigner, then aligning"
	$signed = Join-Path $build "signed.apk"
	Invoke-Native jarsigner @("-keystore", $Keystore, "-storepass", $KeystorePassword, "-sigalg", "SHA256withRSA", "-digestalg", "SHA-256",
		"-signedjar", $signed, $unsigned, $KeyAlias)
	Invoke-Native dotnet @($patcher, "align", $signed, $Out)
	Invoke-Native jarsigner @("-verify", $Out)
}

Write-Host "== Checking the result"
Invoke-Native dotnet @($patcher, "verify", $Apk, $Out, "assemblies/chewie.Droid.dll", "assemblies/ChewieResources.dll", "assemblies/WirePod.dll")
Write-Host "Built $Out"
