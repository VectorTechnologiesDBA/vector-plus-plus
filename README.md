# Vector++

Vector++ patches the original Anki Vector Android app (1.5.0) so it works with a
[wire-pod](https://github.com/kercre123/wire-pod) server instead of Anki's cloud, which no longer exists.

> **Project status:** Vector++ 1.2.4 is the last planned update for the foreseeable future: it does what I set
> out to do. I'm still happy to fix problems, so please report bugs in
> [GitHub Issues](https://github.com/VectorTechnologiesDBA/vector-plus-plus/issues).

This repository contains only the patch: the new code and the build tool. It does **not** contain Anki's app.
You supply your own copy of the original `VECTOR.apk` (version 1.5.0), and the build turns it into
`VECTOR-wirepod.apk`. See [Building the APK](#building-the-apk).

Every image, sound, animation, layout, string, native library and original Java class is kept from the
original `VECTOR.apk` (the build checks that every untouched entry is byte-identical). What changes:

| File | Change |
|---|---|
| `assemblies/WirePod.dll` | New: all wire-pod logic (source in `WirePod/`) |
| `assemblies/chewie.Droid.dll` | Eight small patches that call into `WirePod.dll` |
| `assemblies/ChewieResources.dll` | Analytics off; "Extra features" Settings row; Drive and SSH Home tiles |
| `classes3.dex` | New: the extra screens (source in `android/src/`) plus JSch for SSH |
| `AndroidManifest.xml`, `resources.arsc`, `res/` | Rebuilt by apktool to register the new screens and icons; every existing resource keeps its ID (checked) |
| Android target | Raised from 8.1 (API 27) to 15 (API 35), so newer Android doesn't warn the app is outdated. This needs: plain HTTP allowed (wire-pod), heap pointer tagging off (the 2019 Mono runtime crashes on it), the Android 12+ Bluetooth/notification permissions (asked at startup), `PendingIntent` and runtime-receiver calls in the bundled Java libraries routed through `Compat`, a public replacement for MvvmCross's blocked hidden-API use, and Android 15's edge-to-edge layout turned off |
| `res/font/ttnorms_*.otf` | AAT tables removed so letters like "c" render correctly on newer Android |
| `assets/wirepod/ssh_root_key` | Optional: your robot's SSH key, if you put `ssh_root_key` next to `build.ps1` |

## Features

On top of working with wire-pod, Vector++ adds:

- **Custom eye colors** (backported from app 1.8): any hue and saturation, in Settings right below the standard
  colors, plus the hidden **Confusion Matrix Green** preset (thanks @switch-modder).
- **Check for updates** in **Settings → About**: shows the latest release's notes and installs it.
- **Android 15 support**: no "built for an older version" warning on current phones.
- **Firmware updates** from the Internet Archive (escape-pod 2.0.1.6086ep).

**Settings → Extra features** opens wire-pod's options, styled like the rest of the app, without wire-pod's web page:

- **Weather:** pick OpenWeatherMap or WeatherAPI and enter its API key.
- **Knowledge:** question answering through OpenAI, Together AI, a custom OpenAI-compatible endpoint, or
  Houndify, plus model, prompt and robot name.

Turn on **Enable extra features** to also get **Talk** (Vector says whatever you type), and these, each with its
own switch and Home tile:

- **Drive:** landscape live camera, a joystick for driving, head and lift buttons, and a box to make him talk.
- **SSH console:** a landscape root shell on an OSKR/WireOS robot, using the bundled key.
- **Quick actions:** one tap to start exploring, return to the charger, find his cube, or go to sleep.

Talk, Drive and Quick actions use wire-pod's SDK API, so they need nothing on the robot beyond wire-pod.

> **The bundled SSH key:** if `ssh_root_key` is next to `build.ps1`, it is built into the APK, and setup and
> the SSH console use it without asking. Anyone with that APK can extract the key, so **keep the APK private**.
> `.gitignore` keeps the key, keystore and APKs out of git.

---

## What you need

- **wire-pod** running on your network, with its web page reachable (e.g. `http://192.168.0.100:8080`).
- **An Android phone** on the same Wi-Fi network as wire-pod.
- **Vector**:
  - **OSKR/dev-unlocked robots (e.g. WireOS):** the app can configure him for wire-pod on its own, even
    after a wipe. You need the robot's **SSH key file** (the same file wire-pod's "Configure an
    OSKR/dev-unlocked robot" section asks for).
  - **Production robots:** set them up with wire-pod first (its web page). The app then connects to them.
- **Uninstall the original Vector app** first. This APK is signed with a different key, so Android won't
  install it over the original.

## Install

[Build the APK](#building-the-apk) first. Then copy `VECTOR-wirepod.apk` to the phone and open it, or with USB debugging on:

```bash
adb install VECTOR-wirepod.apk
```

For an OSKR/WireOS robot, also put the SSH key file on the phone (for example in Downloads):

```bash
adb push ssh_root_key /sdcard/Download/
```

## Setting up Vector

1. **Sign in.** Tap **Sign in** (or **Create account**) and enter any email-shaped text (e.g.
   `me@wirepod.local`) and a password with 8–32 characters, including an uppercase letter, a lowercase
   letter and a digit. Nothing is sent anywhere: the account exists only on the phone.
2. **Enter the wire-pod address.** The **wire-pod server** dialog asks where wire-pod is. Enter its IP
   address, e.g. `192.168.0.100`. Add `:port` only if its web page isn't on port 8080. The app checks that
   something answers there. **Use anyway** skips the check.
   - `escapepod.local` is pre-filled, but many phones can't resolve `.local` names, so use the IP.
3. **Connect to Vector over Bluetooth.** Place him on the charger, double-press his back button, and follow
   the app (enter the code shown on his face, then choose your Wi-Fi network).
4. **If Vector isn't pointed at wire-pod yet** (any freshly wiped OSKR/WireOS robot), the app notices at
   the "securing Wi-Fi" step and sets him up automatically:
   1. **First time only:** **Set up Vector for wire-pod** → **Choose key file** → pick the SSH key.
      In the picker, open ☰ → **Downloads** if it isn't under Recent. The key is saved inside the app.
   2. A **Setting up Vector for wire-pod** dialog shows wire-pod's progress. Vector's screen goes blank
      while his services restart. That's normal.
   3. When it says **Vector is set up for wire-pod**, wait until his setup screen is back (about a minute),
      then tap **Try Again** in the app.
5. The app finishes connecting. You're set up.

### Changing the wire-pod address later

Log out (Settings → Account Management → **Log Out**, or **LOG OUT** on the "Searching for Vector" screen)
and sign in again; the dialog appears pre-filled with the current address.

---

## How it works

### Why the stock app can't use wire-pod

| Step | Original app | Problem | With this patch |
|---|---|---|---|
| Sign in | `accounts.api.anki.com` | Gone | Answered on the phone |
| Pair over Bluetooth | Sends a session token to the robot, which asks its **token server** for a client token | Fine: wire-pod is the token server and accepts any token | Unchanged |
| Get the robot's TLS certificate | `session-certs.token.global.anki-services.com/vic/<esn>` | Gone | `http://<wire-pod>:<port>/session-certs/<esn>` |
| Firmware update during setup | `ota.global.anki-services.com/...` | Gone | `http://archive.org/download/Anki-proto-prod-Vector-firmware/prod/2.0.1.6086ep.ota` (escape-pod 2.0.1.6086ep) |
| Analytics | Anki's AWS queue | Account suspended; failed uploads retried forever, flooding the app with threads | Turned off |

### Robots that don't know about wire-pod

When Vector's user data is cleared, his server settings go back to the firmware default (for WireOS,
`vicapi.pvic.xyz`). During setup the app asks him to get a token from his server. He can't reach
wire-pod, so he answers `ConnectionError` (shown as "Vector is having trouble connecting to Wi-Fi").

wire-pod already knows how to fix this for OSKR/dev robots: its `/api-ssh/setup` API logs in over SSH,
installs `server_config.json` (pointing at wire-pod) and wire-pod's certificate, clears the old token,
regenerates the robot's certificates and restarts his services. The app calls that API:

```
robot answers ConnectionError
  → app asks the robot for its status, then its IP (over Bluetooth)
  → POST <wire-pod>/api-ssh/setup   (robot IP + SSH key)
  → poll <wire-pod>/api-ssh/get_setup_status until "done" or an error
  → robot restarts; user taps Try Again; robot now authenticates with wire-pod
```

This runs at most once per robot per app session. If it fails, the dialog shows wire-pod's reason. If
the key is rejected, it's forgotten and you'll be asked for it again.

### The code

`WirePod/` (compiled into `WirePod.dll`):

| File | Purpose |
|---|---|
| `WirePodConfig.cs` | Stores the wire-pod address; builds the session-cert and OTA URLs |
| `ServerPrompt.cs` | The wire-pod server dialog at sign-in, with a reachability check |
| `WirePodHttpService.cs` | Wraps the app's HTTP service; Anki-cloud requests go to `AnkiCloudEmulator`, everything else passes through |
| `AnkiCloudEmulator.cs` | Local answers for the Anki accounts API, location search (echoes your text) and anything else Anki |
| `LocalHttpResponse.cs` | The response object the emulator returns |
| `RobotSetup.cs` | Detects robots that can't reach their server and runs wire-pod's SSH setup |
| `SshKeyStore.cs` | File picker for the SSH key; keeps it in the app's private storage |
| `Ui.cs` | Shared dialog helpers |

Patches to `chewie.Droid.dll` (applied by `Patcher/` with dnlib):

| Method | Patch |
|---|---|
| `AccountService.Configure` | `httpService = WirePodHttpService.Wrap(httpService)` |
| `EmailSubscriptionService.Configure` | same |
| `LocationAutoCompletionService.Configure` | same |
| `RobotCommunicationService.OtaUrl` (getter) | returns `WirePodConfig.OtaUrl(...)` |
| `RobotCommunicationConnectionModule` (auto-connect) | the cert URL's `string.Concat` → `WirePodConfig.SessionCertUrl(...)` |
| `RobotCommunicationService.Initialize` | ends with `RobotSetup.Attach(this)` |

`ChewieResources.dll`: in the embedded `DASConfig.json`, `ShouldSyncNetwork` is set to `false` and
`LogLevels` to `[]`.

### Quirks of the 2019 runtime worth knowing

- **The app's .NET libraries are linker-trimmed.** Many ordinary APIs (e.g. `Intent.AddCategory`,
  `ContentResolver.OpenInputStream`, `MultipartFormDataContent.Add`, `File.WriteAllBytes`) don't exist at
  runtime. `WirePod.dll` is compiled against the exact shipped libraries, so using a missing API fails the
  build rather than crashing the phone. Missing Android calls are made through JNI instead.
- **No new Java classes can be added**, so the key picker receives its result through MvvmCross's
  existing activity-result event rather than an `OnActivityResult` override.
- **Thread floods can deadlock the old Mono runtime on Android 10+** (garbage collector vs. Android's
  thread-list lock). This is why analytics is turned off.
- `DAS.Debug` is compiled out of this build. `WirePod.dll` logs with `DAS.Event` / `DAS.Warn`, which show
  in `adb logcat -s mono-stdout`.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "Can't reach Anki services" right after sign-in | You cancelled the wire-pod dialog. Sign in again. |
| Loops between "Connecting to Wi-Fi" and "having trouble connecting to Wi-Fi" | Vector can't reach wire-pod. For OSKR/WireOS the setup dialog should appear; if it doesn't, check that the wire-pod address is right and wire-pod is running. Production robots: set them up with wire-pod's web page first. |
| "wire-pod couldn't set up Vector … SSH" | Wrong key, or the robot isn't OSKR/dev-unlocked, or wire-pod can't reach the robot. The key is forgotten after a rejection; you'll be asked again. |
| Key file doesn't show in the picker | Open ☰ → **Downloads**. Files copied with `adb push` keep their old date and sort far down in Recent. |
| "Couldn't download robot cert" | wire-pod doesn't have Vector's certificate. It saves it when Vector first authenticates with it; re-run setup. |
| App freezes ("not responding") | Shouldn't happen with analytics off. If it does, capture `adb logcat` and `adb shell dumpsys activity lastanr-traces`. |

Useful logs:

```bash
adb logcat -s mono-stdout
```

wire-pod's own log: `http://<wire-pod>:8080/api/get_debug_logs`.

---

## Building the APK

Windows, with:
- the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0);
- a JDK 11 or newer, e.g. [Temurin](https://adoptium.net/), with `java`, `javac`, `keytool` and `jarsigner` on `PATH`;
- these files in a `tools/` folder (not committed; download them once):
  - [`apktool.jar`](https://bitbucket.org/iBotPeaches/apktool/downloads/apktool_2.10.0.jar) (rename it to `apktool.jar`)
  - [`r8-8.5.35.jar`](https://dl.google.com/android/maven2/com/android/tools/r8/8.5.35/r8-8.5.35.jar) (provides d8)
  - [`android-4.1.1.4.jar`](https://repo1.maven.org/maven2/com/google/android/android/4.1.1.4/android-4.1.1.4.jar) (Android API stubs)
  - [`json-20080701.jar`](https://repo1.maven.org/maven2/org/json/json/20080701/json-20080701.jar)
  - [`jsch-0.2.20.jar`](https://repo1.maven.org/maven2/com/github/mwiede/jsch/0.2.20/jsch-0.2.20.jar)
  - `apksigner.jar` from [Android build-tools 35.0.1](https://dl.google.com/android/repository/build-tools_r35.0.1_windows.zip)
    (inside the zip at `android-15/lib/apksigner.jar`). Required: apps targeting Android 11+ need a v2 signature.
  - `android-35/android.jar` from [Android platform 35](https://dl.google.com/android/repository/platform-35_r02.zip)
    (the Android 15 framework; apktool needs it to build the resources)
- **the original Vector app, `VECTOR.apk` version 1.5.0** (package `com.anki.vector`, versionCode 108).
  It isn't included here; use your own copy.

Steps:

```bash
git clone https://github.com/VectorTechnologiesDBA/vector-plus-plus.git
```

```bash
cd vector-plus-plus
```

Copy your `VECTOR.apk` into this folder, then:

```bash
.\build.cmd
```

The result is `VECTOR-wirepod.apk` in the same folder. dnlib is downloaded from NuGet automatically.

`build.cmd` runs `build.ps1` with PowerShell's script restriction bypassed for that run only. Options
(work with either script): `-Apk <original apk>` (default `VECTOR.apk` next to the script), `-Out <apk>`,
`-DnlibPath <dnlib.dll>` (use a local dnlib instead of NuGet), `-SshKey <file>` (default `ssh_root_key` next to
the script; skipped if missing), `-Tools <dir>`, `-Keystore` / `-KeystorePassword` / `-KeyAlias`.

The build:
1. extracts the app's assemblies from the original APK;
2. compiles `WirePod/*.cs` against them;
3. patches `chewie.Droid.dll` and `ChewieResources.dll`, and fixes the fonts;
4. compiles `android/src/` with JSch into `classes3.dex`, and rebuilds the manifest and resources with apktool;
5. writes a new APK, copying every other entry unchanged; signs it (apksigner if on `PATH`, else
   jarsigner v1) and aligns it;
6. verifies: every patch site found exactly once, every reference resolvable against the shipped
   assemblies, no invalid self/module references, every original entry identical, stored entries aligned.

The first build creates `wirepod-debug.keystore` (password `android`). Keep it: builds signed with the same
key install over each other without losing app data.

## Layout

| Path | What |
|---|---|
| `WirePod/` | Source of `WirePod.dll` |
| `android/` | Java source of the extra screens, and their icons |
| `tools/` | Downloaded build tools (not committed) |
| `ssh_root_key` | Optional robot SSH key to bundle (not committed) |
| `Patcher/` | Build tool: extract, IL patching (dnlib), APK repack/align/verify |
| `build.ps1`, `build.cmd` | Run the whole build |
| `build/` | Intermediate files (recreated each build, not committed) |
| `VECTOR.apk` | Your copy of the original app (not committed) |
| `VECTOR-wirepod.apk` | The result (not committed) |
| `wirepod-debug.keystore` | Signing key, created by the first build (not committed) |

## Disclaimer

Vector++ is an unofficial community project, not affiliated with or endorsed by Anki or Digital Dream Labs.
"Anki" and "Vector" are trademarks of their respective owners. This repository doesn't distribute the
Vector app; you need your own copy.
