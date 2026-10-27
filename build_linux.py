#!/usr/bin/env python3
"""Builds VECTOR-wirepod.apk on Linux: the same steps as build.ps1 (which is Windows-only).

1. Builds the Patcher and extracts the app's .NET assemblies from the original APK.
2. Compiles WirePod/*.cs into WirePod.dll against those exact (linker-trimmed) assemblies.
3. Patches the app's assemblies (see README.md) and fixes the fonts.
4. Compiles the extra screens (android/src) with JSch into classes3.dex.
5. Rebuilds the manifest and resources with apktool, applying the same edits as build.ps1.
6. Packs the APK (every other entry copied byte-for-byte), aligns, signs and verifies it.

Needs the .NET 8 SDK, a JDK 11+ and these in tools/ (the same files build.ps1 uses, see README.md):
apktool.jar, android-35/android.jar, jsch-0.2.20.jar, and r8-8.5.35.jar (for d8). apksigner comes from
tools/apksigner.jar or PATH (Debian/Ubuntu: `apt install apksigner`).

Without r8 (e.g. where dl.google.com is unreachable), the build falls back to dx (Debian/Ubuntu package
dalvik-exchange), which can't handle Java 8 lambdas: build tools/jsch-0.2.20-nolambda.jar with
scripts/jsch_nolambda.py first.
"""
import argparse
import glob
import os
import re
import shutil
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))

ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
ap.add_argument("--apk", default=os.path.join(HERE, "VECTOR.apk"), help="the original Vector app, 1.5.0")
ap.add_argument("--out", default=os.path.join(HERE, "VECTOR-wirepod.apk"))
ap.add_argument("--tools", default=os.path.join(HERE, "tools"))
ap.add_argument("--dnlib", default="", help="use this dnlib.dll instead of the NuGet package")
ap.add_argument("--ssh-key", default=os.path.join(HERE, "ssh_root_key"),
                help="bundled into the APK if it exists. Never commit it or share that APK.")
ap.add_argument("--keystore", default=os.path.join(HERE, "wirepod-debug.keystore"))
ap.add_argument("--keystore-password", default="android")
ap.add_argument("--key-alias", default="wirepod")
args = ap.parse_args()

apk, out, tools = (os.path.abspath(p) for p in (args.apk, args.out, args.tools))
if not os.path.exists(apk):
    sys.exit("Original Vector app not found at %s. Copy VECTOR.apk (version 1.5.0) next to build_linux.py or pass --apk." % apk)
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")


def run(*cmd):
    subprocess.run(cmd, check=True, env=env)


def tool(*parts):
    p = os.path.join(tools, *parts)
    if not os.path.exists(p):
        sys.exit("Missing %s (see README.md: Building the APK)" % p)
    return p


def read(p):
    with open(p, encoding="utf-8") as f:
        return f.read()


def write(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)


def must_replace(s, old, new, what):
    if old not in s:
        sys.exit("%s: %s not found" % (what, old))
    return s.replace(old, new)


android_jar = tool("android-35", "android.jar")
apktool = tool("apktool.jar")
r8 = os.path.join(tools, "r8-8.5.35.jar")
use_d8 = os.path.exists(r8)
if use_d8:
    jsch = tool("jsch-0.2.20.jar")
else:
    dx = shutil.which("dalvik-exchange") or shutil.which("dx")
    if not dx:
        sys.exit("Need tools/r8-8.5.35.jar (d8) or dx on PATH (Debian/Ubuntu package dalvik-exchange)")
    jsch = tool("jsch-0.2.20-nolambda.jar")

build = os.path.join(HERE, "build")
shutil.rmtree(build, ignore_errors=True)
os.makedirs(build)
ref, patched = os.path.join(build, "ref"), os.path.join(build, "patched")

print("== Building Patcher")
cmd = ["dotnet", "build", os.path.join(HERE, "Patcher", "Patcher.csproj"), "-c", "Release", "-nologo", "-v", "q"]
if args.dnlib:
    cmd.append("-p:DnlibPath=" + os.path.abspath(args.dnlib))
run(*cmd)
patcher = os.path.join(HERE, "Patcher", "bin", "Release", "net8.0", "Patcher.dll")

print("== Extracting assemblies from " + apk)
run("dotnet", patcher, "extract", apk, ref)

print("== Compiling WirePod.dll against the app's assemblies")
sdk = subprocess.run(["dotnet", "--list-sdks"], check=True, capture_output=True, text=True, env=env).stdout.strip().splitlines()[-1]
version, root = sdk.split(" [", 1)
csc = os.path.join(root.rstrip("]"), version, "Roslyn", "bincore", "csc.dll")
cscargs = ["-nologo", "-noconfig", "-nostdlib+", "-target:library", "-optimize+", "-deterministic", "-langversion:7.3",
           "-out:" + os.path.join(build, "WirePod.dll")]
cscargs += ["-r:" + p for p in sorted(glob.glob(os.path.join(ref, "*.dll")))]
cscargs += sorted(glob.glob(os.path.join(HERE, "WirePod", "*.cs")))
run("dotnet", csc, *cscargs)

print("== Patching the app's assemblies")
run("dotnet", patcher, "patch", ref, os.path.join(build, "WirePod.dll"), patched)

print("== Fixing fonts (removing AAT tables that newer Android renders oddly)")
fonts = os.path.join(build, "fonts")
run("dotnet", patcher, "fonts", apk, fonts)

print("== Compiling the extra screens (Java) into classes3.dex")
java = os.path.join(build, "java")
os.makedirs(os.path.join(java, "cls"))
os.makedirs(os.path.join(java, "dex"))
run("javac", "--release", "8", "-nowarn", "-encoding", "UTF-8", "-cp", android_jar + os.pathsep + jsch,
    "-d", os.path.join(java, "cls"), *sorted(glob.glob(os.path.join(HERE, "android", "src", "**", "*.java"), recursive=True)))
classes = sorted(glob.glob(os.path.join(java, "cls", "**", "*.class"), recursive=True))
if use_d8:
    run("java", "-cp", r8, "com.android.tools.r8.D8", "--release", "--min-api", "26", "--lib", android_jar,
        "--output", os.path.join(java, "dex"), jsch, *classes)
else:
    run(dx, "--dex", "--min-sdk-version=26", "--output=" + os.path.join(java, "dex", "classes.dex"),
        os.path.join(java, "cls"), jsch)

print("== Rebuilding manifest, resources and dex (new screens and icons, Android 15 target; resource IDs are kept)")
res = os.path.join(build, "res")
src = os.path.join(res, "src")
run("java", "-jar", apktool, "d", "-f", "-o", src, apk)
for f in glob.glob(os.path.join(HERE, "android", "res", "drawable", "*.xml")):
    shutil.copy(f, os.path.join(src, "res", "drawable"))

# The same manifest, smali and resource edits as build.ps1; see the comments there.
manifest = os.path.join(src, "AndroidManifest.xml")
activities = ["ExtrasActivity", "WeatherActivity", "KnowledgeActivity", "TalkActivity", "DriveActivity", "SshActivity",
              "EyeColorActivity", "UpdateActivity", "QuickActionsActivity", "RemoteActivity"]
lines = []
for a in activities:
    orientation = "sensorLandscape" if a in ("DriveActivity", "SshActivity") else "portrait"
    single_top = ' android:launchMode="singleTop"' if a == "UpdateActivity" else ""
    lines.append('        <activity android:name="com.wirepod.extras.%s" android:theme="@android:style/Theme.Material.NoActionBar" '
                 'android:screenOrientation="%s"%s/>' % (a, orientation, single_top))
text = must_replace(read(manifest), "</application>", "\n".join(lines) + "\n    </application>", "AndroidManifest.xml")
yml = os.path.join(src, "apktool.yml")
write(yml, re.sub(r"targetSdkVersion: '?\d+'?", "targetSdkVersion: '35'", read(yml)))
text = must_replace(text, '<activity android:icon="@mipmap/ic_launcher" android:name=',
                    '<activity android:exported="true" android:icon="@mipmap/ic_launcher" android:name=', "AndroidManifest.xml")
text = must_replace(text, '<application android:allowBackup="true"',
                    '<application android:allowNativeHeapPointerTagging="false" android:usesCleartextTraffic="true" android:allowBackup="true"',
                    "AndroidManifest.xml")
text = must_replace(text, '<receiver android:name="com.appboy.AppboyFcmReceiver"',
                    '<receiver android:exported="true" android:name="com.appboy.AppboyFcmReceiver"', "AndroidManifest.xml")
bluetooth = '<uses-permission android:name="android.permission.BLUETOOTH"/>'
text = must_replace(text, bluetooth, bluetooth +
                    '\n    <uses-permission android:name="android.permission.BLUETOOTH_SCAN" android:usesPermissionFlags="neverForLocation"/>'
                    '\n    <uses-permission android:name="android.permission.BLUETOOTH_CONNECT"/>'
                    '\n    <uses-permission android:name="android.permission.ACCESS_FINE_LOCATION"/>'
                    '\n    <uses-permission android:name="android.permission.POST_NOTIFICATIONS"/>'
                    '\n    <uses-permission android:name="android.permission.REQUEST_INSTALL_PACKAGES"/>'
                    '\n    <queries><intent><action android:name="android.intent.action.VIEW"/><data android:scheme="https"/></intent>'
                    '<intent><action android:name="android.intent.action.SENDTO"/><data android:scheme="mailto"/></intent></queries>',
                    "AndroidManifest.xml")
write(manifest, text)

pending_intent = re.compile(r"Landroid/app/PendingIntent;->(getActivity|getActivities|getBroadcast|getService)\(")
register_receiver = re.compile(r"invoke-virtual(/range)? (\{[^}]*\}), Landroid/content/Context;->registerReceiver\(")
rewired = 0
for smali in glob.glob(os.path.join(src, "**", "*.smali"), recursive=True):
    code = read(smali)
    new = pending_intent.sub(r"Lcom/wirepod/extras/Compat;->\1(", code)
    new = register_receiver.sub(lambda m: "invoke-static%s %s, Lcom/wirepod/extras/Compat;->registerReceiver(Landroid/content/Context;"
                                % (m.group(1) or "", m.group(2)), new)
    if new != code:
        write(smali, new)
        rewired += 1
print("   PendingIntent/registerReceiver calls rewired in %d classes" % rewired)

styles = os.path.join(src, "res", "values", "styles.xml")
opt_out = '\n        <item name="android:windowOptOutEdgeToEdgeEnforcement">true</item>'
xml = re.sub(r'(<style name="Chewie[^"]*"[^>]*?)\s*/>', lambda m: m.group(1) + ">" + opt_out + "\n    </style>", read(styles))
xml = re.sub(r'(<style name="Chewie[^"]*"[^>]*[^/]>)(?!\s*<item name="android:windowOptOut)', lambda m: m.group(1) + opt_out, xml)
write(styles, xml)

strings = os.path.join(src, "res", "values", "strings.xml")
sx = read(strings)
for old, new in [("https://go.anki.com/vector-app_vector_hc_troubleshooting",
                  "https://support.anki.bot/article/104-troubleshooting-vectore28099s-connection"),
                 ("https://go.anki.com/vector-app_vector_hc_category", "https://support.anki.bot/collection/6-vector"),
                 ("https://go.anki.com/vector-app_product_page", "https://anki.bot/products/vector-robot")]:
    sx = must_replace(sx, old, new, "strings.xml")
sx, n1 = re.subn(r'(<string name="SignUpLoginView\.agreement">)[^<]*(</string>)',
                 r"\1Your account only exists on this phone. Nothing is sent to Anki.\2", sx)
sx, n2 = re.subn(r'(<string name="WelcomeAgreementView\.messageLink">)[^<]*(</string>)', r"\1\2", sx)
if (n1, n2) != (1, 1):
    sys.exit("strings.xml: agreement strings not found")
write(strings, sx)

framework = os.path.join(tools, "apktool-framework")
if not os.path.exists(os.path.join(framework, "1.apk")):
    run("java", "-jar", apktool, "if", android_jar, "-p", framework)
run("java", "-jar", apktool, "b", "-p", framework, "-o", os.path.join(res, "res.zip"), src)
res_all, res_out = os.path.join(res, "all"), os.path.join(res, "out")
with zipfile.ZipFile(os.path.join(res, "res.zip")) as z:
    z.extractall(res_all)
os.makedirs(res_out)
for name in ["AndroidManifest.xml", "resources.arsc", "res", "classes.dex", "classes2.dex"]:
    shutil.move(os.path.join(res_all, name), os.path.join(res_out, name))

print("== Packing APK")
unsigned = os.path.join(build, "unsigned.apk")
replaced = ["assemblies/%s=%s" % (dll, os.path.join(patched, dll)) for dll in
            ["chewie.Droid.dll", "ChewieResources.dll", "WirePod.dll", "MvvmCross.Binding.Droid.dll", "core.Droid.dll"]]
replaced.append("@" + res_out)
# After the rebuilt res/ so the fixed fonts win.
replaced += ["res/font/%s=%s" % (os.path.basename(f), f) for f in sorted(glob.glob(os.path.join(fonts, "*.otf")))]
replaced.append("classes3.dex=" + os.path.join(java, "dex", "classes.dex"))
if os.path.exists(args.ssh_key):
    print("   Bundling SSH key %s (keep this APK private)" % args.ssh_key)
    replaced.append("assets/wirepod/ssh_root_key=" + os.path.abspath(args.ssh_key))
else:
    print("   No SSH key at %s; setup will ask for the key file instead" % args.ssh_key)
run("dotnet", patcher, "pack", apk, unsigned, *replaced)

if not os.path.exists(args.keystore):
    print("== Creating signing key " + args.keystore)
    run("keytool", "-genkeypair", "-keystore", args.keystore, "-storepass", args.keystore_password,
        "-keypass", args.keystore_password, "-alias", args.key_alias, "-keyalg", "RSA", "-keysize", "2048",
        "-validity", "10000", "-dname", "CN=Vector wire-pod build")

print("== Aligning and signing with apksigner (v1 + v2 + v3)")
apksigner_jar = os.path.join(tools, "apksigner.jar")
signer = ["java", "-jar", apksigner_jar] if os.path.exists(apksigner_jar) else [shutil.which("apksigner") or ""]
if not signer[0]:
    sys.exit("apksigner not found. Put apksigner.jar in tools/ or install apksigner.")
run("dotnet", patcher, "align", unsigned, out)
run(*signer, "sign", "--ks", args.keystore, "--ks-pass", "pass:" + args.keystore_password, "--ks-key-alias", args.key_alias, out)
run(*signer, "verify", out)

print("== Checking the result")
run("dotnet", patcher, "verify", apk, out, *replaced)
print("Built " + out)
