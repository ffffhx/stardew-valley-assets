"""Build and run a separate reconstruction against an installed Stardew Valley 1.6.15.

Python 3.9+; .NET SDK 8+. Never writes to the supplied game installation.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parent
ORIGINAL_WINDOWS_SHA256 = "7f1e5b8e58d2758b78570ba771bbeb03d33522f62188bf6c32edf0cf626deaee"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("build", "prepare", "launch", "all"))
    parser.add_argument("--game", default=os.getenv("STARDEW_GAME_PATH"), required=not os.getenv("STARDEW_GAME_PATH"))
    parser.add_argument("--dotnet", help="Path to a .NET 8+ SDK's dotnet executable")
    parser.add_argument("--smoke-test", action="store_true", help="Create an isolated test farm and exercise saving/loading")
    args = parser.parse_args()
    game = Path(args.game).expanduser().resolve()
    runtime = ROOT / "runtime"
    if game == runtime or ROOT == game or ROOT in game.parents or game in ROOT.parents:
        parser.error("The source installation must be separate from the reconstruction directory")
    for required in ("Stardew Valley.dll", "StardewValley.GameData.dll", "MonoGame.Framework.dll", "Content"):
        if not (game / required).exists():
            parser.error(f"Missing installed game dependency: {required}")
    if os.name == "nt":
        digest = hashlib.sha256((game / "Stardew Valley.dll").read_bytes()).hexdigest()
        if digest != ORIGINAL_WINDOWS_SHA256:
            parser.error("This reconstruction requires the verified Windows Steam 1.6.15.24356 assembly")
    env = os.environ.copy()
    env.update(STARDEW_GAME_PATH=str(game), DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_UI_LANGUAGE="en")
    logs = ROOT / "logs"
    logs.mkdir(exist_ok=True)
    if args.command in ("build", "all"):
        local_dotnet = ROOT.parent / "tools" / "dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
        dotnet = args.dotnet or (str(local_dotnet) if local_dotnet.exists() else shutil.which("dotnet"))
        if not dotnet:
            parser.error("Install .NET SDK 8+ or supply --dotnet")
        local_nuget = ROOT.parent / "tools" / "nuget-packages"
        if local_nuget.exists():
            env["NUGET_PACKAGES"] = str(local_nuget)
        with (logs / "build.log").open("w", encoding="utf-8") as output:
            result = subprocess.run([dotnet, "build", str(ROOT / "src/game/Stardew Valley.csproj"), "-c", "Release", "-v", "minimal"], env=env, stdout=output, stderr=subprocess.STDOUT)
        build_text = (logs / "build.log").read_text(encoding="utf-8")
        print(build_text if result.returncode else "\n".join(build_text.splitlines()[-5:]))
        if result.returncode:
            return result.returncode
    if args.command in ("prepare", "all"):
        output = ROOT / "src/game/bin/Release/net6.0"
        for name in ("Stardew Valley.dll", "StardewValley.GameData.dll"):
            if not (output / name).exists():
                parser.error("Build before preparing the runtime")
        # Keep the installed platform's apphost, deps, native libraries and runtime.
        # Do not copy generated .deps.json over the game's self-contained deployment.
        shutil.copytree(game, runtime, dirs_exist_ok=True)
        for name in ("Stardew Valley", "StardewValley.GameData"):
            for suffix in (".dll", ".pdb"):
                source = output / (name + suffix)
                if source.exists():
                    shutil.copy2(source, runtime / source.name)
        print(f"Prepared isolated runtime: {runtime}")
    if args.command in ("launch", "all"):
        if sys.platform != "win32":
            parser.error("Automatic launch is verified on Windows only; use this platform's original apphost in runtime/ with STARDEW_RECONSTRUCTION_DATA_DIR set")
        executable = runtime / "Stardew Valley.exe"
        if not executable.exists():
            parser.error("Prepare the runtime before launching")
        data = ROOT / "test-data" / time.strftime("%Y%m%d-%H%M%S") if args.smoke_test else ROOT / "runtime-data"
        data.mkdir(parents=True, exist_ok=True)
        env["STARDEW_RECONSTRUCTION_DATA_DIR"] = str(data.resolve())
        env["STARDEW_RECONSTRUCTION_REPORT"] = str((data / "smoke-test.json").resolve())
        env["SteamAppId"] = "413150"
        command = [str(executable)] + (["--reconstruction-smoke-test"] if args.smoke_test else [])
        with (data / "stdout.log").open("w", encoding="utf-8") as stdout, (data / "stderr.log").open("w", encoding="utf-8") as stderr:
            proc = subprocess.Popen(command, cwd=runtime, env=env, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr,
                                    creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP, close_fds=True)
        record = {"pid": proc.pid, "executable": str(executable), "data": str(data), "smoke_test": args.smoke_test}
        (logs / "launch.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
        time.sleep(2)
        if proc.poll() is not None:
            print(f"Launch exited with {proc.returncode}; inspect {data}", file=sys.stderr)
            return 1
        print(json.dumps(record, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
