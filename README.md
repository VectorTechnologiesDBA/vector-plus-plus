# Vector++

Vector++ patches the original Anki Vector Android app (1.5.0) so it works with a
[wire-pod](https://github.com/kercre123/wire-pod) server instead of Anki's cloud, which no longer exists.

This repository contains only the patch: the new code and the build tool. It does **not** contain Anki's app.
You supply your own copy of the original `VECTOR.apk` (version 1.5.0), and the build turns it into
`VECTOR-wirepod.apk`. See [Building the APK](#building-the-apk).

Every resource, image, sound, animation, layout, string, native library and Java class is copied from the
original `VECTOR.apk` byte-for-byte (the build checks this). Only three files inside the APK change:

| File | Change |
|---|---|
| `assemblies/WirePod.dll` | New: all wire-pod logic (source in `WirePod/`) |
| `assemblies/chewie.Droid.dll` | Six small patches that call into `WirePod.dll` |
| `assemblies/ChewieResources.dll` | Analytics turned off in its embedded config |

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
| Firmware update during setup | `ota.global.anki-services.com/...` | Gone | `http://<wire-pod>:<port>/api/get_ota/vicos-2.0.1.6076ep.ota` |
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
- a JDK, e.g. [Temurin](https://adoptium.net/), with `keytool` and `jarsigner` on `PATH`;
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
`-DnlibPath <dnlib.dll>` (use a local dnlib instead of NuGet), `-Keystore` / `-KeystorePassword` / `-KeyAlias`.

The build:
1. extracts the app's assemblies from the original APK;
2. compiles `WirePod/*.cs` against them;
3. patches `chewie.Droid.dll` and `ChewieResources.dll`;
4. writes a new APK, copying every other entry unchanged; signs it (apksigner if on `PATH`, else
   jarsigner v1) and aligns it;
5. verifies: every patch site found exactly once, every reference resolvable against the shipped
   assemblies, no invalid self/module references, every original entry identical, stored entries aligned.

The first build creates `wirepod-debug.keystore` (password `android`). Keep it: builds signed with the same
key install over each other without losing app data.

## Layout

| Path | What |
|---|---|
| `WirePod/` | Source of `WirePod.dll` |
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
