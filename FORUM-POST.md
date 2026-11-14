# Forum post — English

Copy everything between the two rules into the forum. It is written to stand on its own, is English-only, and does not depend on the release page being read first.

---

**Shadow Gambit Co-op — unofficial two-player co-op (test build)**

Unofficial, experimental co-op for **Shadow Gambit: The Cursed Crew**. One player hosts and runs the authoritative game; the other sends commands and receives state and checkpoint updates. Not affiliated with or endorsed by Mimimi Games, Unity or Valve.

**Requirements**
- Windows x64 and the game itself.
- Internet for the first setup and the first game launch.
- BepInEx and Python are **not** inside this download; the included installer fetches them from their official sites.

**Install**
1. Close the game on both PCs.
2. Extract the ZIP into the game folder.
3. Open PowerShell in that folder and run:
   `powershell -NoProfile -ExecutionPolicy Bypass -File .\ShadowGambitCoop\Check-Environment.ps1`
   It lists what is present and what is missing, and names the fix for each gap.
4. If something is missing, run once:
   `powershell -NoProfile -ExecutionPolicy Bypass -File .\ShadowGambitCoop\Install-Dependencies.ps1`
   It downloads BepInEx 6.0.0-be.682 (IL2CPP win-x64) from builds.bepinex.dev and CPython 3.13.9 embeddable from python.org, verifies pinned SHA-256 values before extracting, backs up whatever it replaces and rolls back on failure. It does not touch saves and does not start the game. On a machine without internet, pass `-OfflineCache <folder>` holding those two ZIPs.
5. Launch the game once. BepInEx generates the IL2CPP interop and downloads the Unity base libraries, so this first launch needs network; afterwards the game starts offline.
6. Both PCs must run the same build.

**What you get**
- Host-authoritative two-player co-op: movement, skills, interactions, guards, bodies and story waits.
- Scene pings: `Alt+Left click`, `Ctrl+E+Left click`, or the backquote key. One marker per player, about five seconds, host cyan / guest amber. Never issues movement, attack or view-cone commands.
- `F11` disconnect, `F7` resume host, `F6` guest reconnect. `F8` is untouched and remains the game's own quickload.
- The ping sound is a short bell at `ShadowGambitCoop/media/bell_b.wav`; replace it with any 16-bit PCM WAV if you prefer.

**Known issues**
- The ping feature has had **no two-player test**. Rendering, colours, sound and input suppression are only checked against test doubles.
- Dense enemy scenes fall behind. With around 64 ordinary guards the update interval can exceed the 0.55 s view lease, so distant guards turn in visible steps.
- An earlier report of "create room, then Continue / Load is blocked" still awaits confirmation.
- A separate multiplayer (3+ players) experiment exists but is **not** part of this download and should not be used for normal play.

**Reporting a problem**
Include the build you downloaded, the role that broke (host or guest), how you connected (LAN, overlay, TCP, TCP+UDP, HTTPS), and the collected logs from **both** PCs from the **same** session. A report with one side's logs cannot be diagnosed. Do not paste room links or account details in public.

Issues: https://github.com/hamster4477/shadow-gambit-coop/issues
Email: hamster4477@qq.com (sensitive material only)

**Source and checksums**
Source is MIT-licensed: https://github.com/hamster4477/shadow-gambit-coop
This download is a mod-only package. Verify its SHA-256 against the value published on the release page before running anything.

---

## Notes for the maintainer

- Do not post the multiplayer trial in the same thread. It shares the plugin GUID and DLL file name with this build, so installing one overwrites the other, and it has never been played on more than two PCs.
- If the forum forbids installers that download and execute other programs, link the release page instead of attaching the ZIP, and say plainly that the scripts are open source and only fetch the two official archives listed above.
- Both scripts are unsigned and run with `-ExecutionPolicy Bypass`. Some antivirus and SmartScreen setups warn about that pattern; mentioning it up front avoids the question coming back as an accusation.
