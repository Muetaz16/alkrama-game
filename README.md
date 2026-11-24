# Al-Karama — Benghazi Corniche (Unity prototype)

First-person military shooter prototype set on the **Corniche of Benghazi, Libya**: the Sidi Khreibish
lighthouse, the seafront road and roundabout, the beach and breakwater, and war-damaged old-city streets.
Built to show investors. Tone: realistic, grounded, Call of Duty / Battlefield feel.

> **For an AI agent picking this up:** read this whole file first. It tells you how the project is
> generated (almost everything is built by editor scripts, not by hand), how to build and test it
> headlessly, and the constraints the owner has set.

---

## 1. Setup

| Need | Version / note |
|---|---|
| Unity | **6000.6.8f1** (Unity 6) — see `ProjectSettings/ProjectVersion.txt` |
| Render pipeline | URP 17.6 (`Assets/Settings/URP_PC.asset`, `URP_Mobile.asset`) |
| Packages | glTFast 6.20, Input System 1.20, AI Navigation 2.0.16, Cinemachine 6.6 (see `Packages/manifest.json`) |
| Git LFS | **Required.** All `*.fbx` are in LFS (`Soldier.fbx` is 112 MB). Run `git lfs install` before cloning. |
| OS used so far | Windows 11, RTX 4050 laptop — ~140 fps at 1600×900 |

```bash
git lfs install
git clone https://github.com/Muetaz16/alkrama-game.git
```

Open the folder in Unity Hub (Unity 6000.6.8f1). The main scene is **`Assets/Scenes/Corniche.unity`**.
Unity regenerates `Library/`, `Temp/`, `Logs/` (git-ignored). The first import takes a while.

**Licence gotcha:** headless (`-batchmode`) runs fail with exit code **198** / `No valid Unity Editor license
found` when the Unity Hub sign-in has expired. Fix = the human signs in to Unity Hub again (never try to
sign in for them). This has happened many times.

---

## 2. Controls (Windows build)

| Key | Action |
|---|---|
| WASD / Shift / C / Space | move / sprint (double-tap = tactical sprint) / crouch / jump |
| Mouse, LMB, RMB | look, fire, aim down sights (sniper: scope) |
| R | reload (tactical if rounds left, emergency if empty) |
| **Q / E** | lean left / right (peek round corners) |
| 1 / 2 / 3, wheel | AK-47 / sniper (VSS) / knife |
| F | quick knife |
| V | first-person ⇄ third-person |
| Shift while scoped | hold breath |
| M | drop a map marker (debug) |
| Esc | free the mouse |

---

## 3. How the project is generated (important)

The scene is **not hand-made**. It is produced by a chain of editor scripts in `Assets/Editor/`, run in
batch mode. If you change a generator, re-run it — don't edit the generated objects by hand, they get
overwritten.

### Full rebuild order

```
SoldierSetup.Run      → imports soldier + Mixamo clips, builds Player.prefab, Enemy.prefab, animator controllers
LookUpgrade.All       → SceneBuilder.Build (city GLB, sea, sun, URP, post FX)
                         → SoldierSetup.PlaceInScene
                         → LookUpgrade.Run (textures, materials, SSAO, water, sea floor, lighting)
                         → Seafront.Build   (beach, balustrade, palm median, real palms)
                         → CityDetail.Build (kerbs, sidewalks, road marks/wear, lamps, poles, cars, checkpoints, shop fronts, rubble)
                         → LookPro.Build    (Poly Haven sky + scanned textures + scanned boulders)
                         → GameplaySetup.Run (weapons, health, NavMesh bake, enemy spawner, practice targets)
SoldierSetup.BuildWindows → builds ../builds/win/Benghazi.exe
```

Partial re-runs: `CityDetail.Run`, `Seafront.Run`, `LookPro.Run`, `GameplaySetup.NavOnly`.
Debug tools: `NavDebug.Run` (top-down NavMesh picture), `ShowcaseShots.Run`, `FindSpot.Run`.

### Batch command (PowerShell)

```powershell
$u = "C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor\Unity.exe"
$root = "C:\Users\<you>\Desktop\tasks 3D"; $p = "$root\BenghaziUnity"
Start-Process -FilePath $u -ArgumentList @("-batchmode","-quit","-projectPath","`"$p`"","-executeMethod","LookUpgrade.All","-logFile","`"$root\unity-look.log`"") -Wait
Start-Process -FilePath $u -ArgumentList @("-batchmode","-projectPath","`"$p`"","-executeMethod","SoldierSetup.BuildWindows","-logFile","`"$root\unity-winbuild.log`"") -Wait
```
(`BuildWindows` calls `EditorApplication.Exit`, so no `-quit`.) Scripts-only changes need just `BuildWindows`.
Generator changes need `LookUpgrade.All` (≈10 min) or the matching partial method.

### Fast compile check without Unity
Compiling all `Assets/**/*.cs` with the editor's bundled Roslyn (`Editor/Data/DotNetSdk/sdk/*/Roslyn/bincore/csc.dll`)
against `Editor/Data/Managed/UnityEngine/*.dll` + `Library/ScriptAssemblies/Unity.*.dll` catches errors in
seconds. Leave out the duplicate `Managed/UnityEditor.dll` and `Managed/UnityEngine.dll` or you get CS0433.

---

## 4. Automated play-test

Run the built game with `-autotest`:

```powershell
& "$root\builds\win\Benghazi.exe" -autotest -screen-fullscreen 0 -screen-width 1600 -screen-height 900 -logFile "$root\unity-player.log"
```

`Assets/Benghazi/Scripts/AutoTest.cs` then drives the player (≈3–4 min) and writes to **`../unity_shots/`**:
- `autotest.log` — load time, fps per movement step, rifle/sniper/knife hits, tactical vs emergency reload
  ammo counts, lean offset, enemy behaviour (modes, cover, path-finding), drowning, respawn.
- screenshots: `fp_*.png` (weapons, reloads, lean, low ready), `seafront_*.png`, `watch_*.png` (enemies),
  `enemy_*.png`, `showcase_*.png`, `tp_view.png`.

Check `unity-player.log` for `Exception` (should be 0). Kill any leftover `Benghazi.exe` before a run, or the
new one can quit at once. The player has `runInBackground = true` so it keeps running unfocused.
The test resets nothing it saves: clear `HKCU\Software\DefaultCompany\BenghaziUnity\benghazi_best_kills*`
afterwards so the owner's "BEST" kill record starts at 0.

---

## 5. Code map — `Assets/Benghazi/Scripts/`

| File | What it does |
|---|---|
| `PlayerController.cs` | Movement (ported from the web prototype: jog 4.2, sprint 7.5, tactical 9.5 m/s, momentum), crouch, jump, **lean (Q/E)**. |
| `ThirdPersonCamera.cs` | First person by default (eye 1.62 m, crouch 1.08), head bob, sprint FOV, **lean offset + head roll**, roll into turns; third person over-the-shoulder with wall avoidance. Layer cull distances for street detail. |
| `WeaponController.cs` | Weapons (AK-47, VSS sniper, knife), input, **ballistics** (715 / 850 m/s, gravity, 6.5 cm bore under sights, zero 100 m / 200 m), recoil pattern + recovery, spread model, fatigue & breathing sway, **tactical vs emergency reload** ammo logic (tactical = 30+1), scope overlay, HUD, shells, impacts, generated sounds, DOF focus when aiming. |
| `ViewModel.cs` | First-person gun: hip/ADS poses, sway, bob, sprint pose, **low ready / compressed ready / high port**, recoil springs, knife slash/stab, weapon switch, **reload choreography** (workspace, fresh mag strips old one, rock-in, drop or stow, charging-handle rack) and **first-person arms** (a copy of the soldier, two-bone IK onto grip/handguard/magazine/handle, fist on the knife). |
| `GunFactory.cs` | Loads the real CC0 guns from `Assets/Resources/Guns/` (falls back to code-built models). Named nodes used by animation: `Muzzle`, `Magazine`, `Bolt`, `Sight`, `Eject`, `GripL`. |
| `EnemyAI.cs` | Enemy soldier on the NavMesh, behaviour ported from the web game: patrol, hunt, real cover (crouch / peek / reload), flanker (1 in 3), open-ground strafing, grenades, flinch, death clips; upper-body aim, left-hand IK, footsteps, near-miss whiz. Also `EnemyGrenade`. |
| `CombatAudio.cs` | **All sound, synthesised in code** (no audio files): layered gunshots (crack / body / action / echo tail, several takes), enemy fire delayed and dulled by distance, near-miss crack, footsteps by surface on the player's step phase, kit, reload parts, brass, impacts by surface, grenade blast with ear-ring + ducking, wind / surf / far-off battle. Clips are made on background threads at start; pooled voices (24 world + 10 local). Tunables are the public statics at the top. |
| `CombatFx.cs` | **All combat visuals, built in code**: muzzle flash core / light / smoke, tracers that fly as streaks, impacts by surface (`CombatSurface` reads material + object names), blood mist, case smoke, grenade explosion, camera shake (`CombatFx.Shake`, added to `ThirdPersonCamera.lookDelta` as a zero-sum wobble; off while the camera is script-driven). Only the `Sprites/Default` shader. |
| `EnemySpawner.cs` | Keeps 6–8 enemies alive, spawns 45–150 m away out of sight on street spots. |
| `PlayerHealth.cs` | Health, regen, hurt vignette + hit-direction marker, death/respawn, **drowning** (breath bar in deep water), kill counter + saved best (PlayerPrefs). |
| `Damageable.cs` | Base class for anything that can be shot (targets, enemies). |
| `PalmSway.cs`, `EnvironmentInit.cs`, `MarkerTool.cs` | Palm crowns in the wind; reflection probe refresh; M-key map marker. |
| `AutoTest.cs` | The scripted play-test (section 4). |

`Assets/Editor/` — the generators listed in section 3, plus `RealCars.cs` (downloaded car models → colour /
shot-up / burnt variants, LODs) and `ProjectSetup.cs` (package install).

### Data
- `Assets/Benghazi/Data/benghazi_corniche.json` — the map (roads, building footprints, sea strips, coast,
  landmarks) produced from OpenStreetMap by the web prototype's `tools/osm2map.py`.
- `Assets/Benghazi/Models/benghazi_corniche_city.glb` + `Data/benghazi_corniche_spawns.json` — the city
  geometry exported from the web prototype (see section 6). Coordinates: **web x = Unity −x**, z unchanged.

---

## 6. The web prototype (source of the city)

The city mesh comes from a separate **Three.js** prototype (`Desktop\tasks 3D\prototype`, *not in this repo*):
`tools/osm2map.py` turns an Overpass/OSM extract into the map JSON (current command uses
`LIGHTHOUSE_SEA=1 PALACE_AWAY=-1 LIGHTHOUSE_AT="side:60"` — lighthouse beside the seafront road facing the
sea, nothing built between it and the water). The page runs on Vite (`npm run dev`, port 5173), and the city
is exported to GLB from the browser with three.js `GLTFExporter` through a dev-only `/__save` endpoint into
`../unity_import/`, then copied into `Assets/Benghazi/Models` and `Data`. Wait until the lighthouse tower
model has loaded before exporting (it loads async; exporting early leaves it out).

---

## 7. Assets and credits (keep these)

- Map data © **OpenStreetMap contributors** (ODbL) — credit must stay visible.
- Soldier + animations: Mixamo (Ch15 + Pro Rifle Pack).
- Guns (CC0, OpenGameArt): AK-47 by Lamoot, VSS "Soviet Special Sniper Rifle 3D", KA-BAR knife by gamekorp —
  see `Assets/Resources/Guns/CREDITS.txt`.
- Sky (kloofendal_48d_partly_cloudy), 2K textures, boulder: **Poly Haven, CC0**.
- Cars: owner-supplied AI-generated (Tripo) sedan + SUV, re-baked with `prototype/tools/rebake-car.mjs`.

---

## 8. Owner's rules and preferences

- **Never sign in or enter credentials** for the owner (Unity Hub, GitHub …) — ask them to do it.
- **Ask before downloading files**; prefer CC0 sources. Reference photos are for reference only.
- **No ISIS (or any extremist) symbols** anywhere in the game.
- Keep the OpenStreetMap credit.
- The owner speaks Arabic and English; screenshots are the best way to report progress.
- Realism is the goal (investor demo): the owner compares against Call of Duty / Battlefield and judges by feel.
- Commit as `sd.team <sd.team@iph-ly.com>`; the repo uses Git LFS for `*.fbx`.

---

## 9. Current state (Nov 2026)

Working and play-tested (0 exceptions, ~100–140 fps):
- Real OSM layout of the Corniche, lighthouse at the seafront, roundabout, arcade palace, damaged buildings,
  beach + surf + scanned boulders, balustrade, palm median, swaying palms, real sky, scanned surfaces.
- First person with gloved arms holding real AK-47 / VSS / KA-BAR; elite handling (lean, ready postures,
  tactical/emergency reloads), real ballistics and recoil.
- Enemies with NavMesh, cover, flanking, grenades; health, drowning, kill record.

Known weak spots / good next steps:
1. **Enemy movement feel** — owner judged it "still way off"; the JS prototype's enemies felt better. The
   behaviour has been ported (see `EnemyAI.cs` header) but needs play-testing and tuning with the owner.
2. Buildings are still the web export's boxy blocks with painted facades — next big visual upgrade
   (detailed Italian arcades / war-damaged multi-storey blocks like the reference photos).
3. Burnt cars are a single rust colour; sunlit walls a bit over-yellow.
4. Sniper scope sway makes the auto-test's 160 m shot hit or miss at random (realistic, hold Shift).
5. `CityDetail.asset` is 56 MB (GitHub warns over 50 MB) — could be split or moved to LFS.
6. No mission/objective, no main menu, no mobile controls yet.
