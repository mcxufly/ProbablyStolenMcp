"""Package an exact allow-list; never install into the game or include game DLLs."""
from pathlib import Path
import hashlib
import shutil
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent
MOD = ROOT / "dist" / "ProbablyStolenMcp"
FILES = {
    "ProbablyStolenMcp.dll": ROOT / "bin/Release/netstandard2.1/ProbablyStolenMcp.dll",
    "manifest.xml": ROOT / "assets/manifest.xml",
    "Localization/en.csv": ROOT / "assets/Localization/en.csv",
    "Localization/zh.csv": ROOT / "assets/Localization/zh.csv",
}


def main():
    MOD.mkdir(parents=True, exist_ok=True)
    existing = {str(p.relative_to(MOD)) for p in MOD.rglob("*") if p.is_file()}
    if existing - FILES.keys():
        raise SystemExit("Unexpected dist files; inspect manually before packaging: " + str(existing - FILES.keys()))
    for relative, source in FILES.items():
        dest = MOD / relative
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, dest)
    manifest = ET.parse(MOD / "manifest.xml").getroot()
    version = manifest.findtext("ModVersion")
    archive = ROOT / "dist" / f"ProbablyStolenMcp-{version}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for relative in sorted(FILES):
            z.write(MOD / relative, "Mods/ProbablyStolenMcp/" + relative)
        # Python stays OUTSIDE Mods: the native scanner rejects scripts in a mod.
        for source in sorted((ROOT / "bridge").glob("*.py")):
            z.write(source, "mcp/bridge/" + source.name)
        z.write(ROOT / "README.md", "README.md")
        z.write(ROOT / "mcp-config.example.json", "mcp-config.example.json")
        z.write(ROOT / "AGENT.md", "AGENT.md")
        for name in ("API-NOTES.md", "RESULTS.md", "LIVE-2026-11-13.md"):
            z.write(ROOT / "verification" / name, "verification/" + name)
    source_archive = ROOT / "dist" / f"ProbablyStolenMcp-{version}-source.zip"
    top_level = (".gitignore", "ProbablyStolenMcp.csproj", "build.sh", "package.py",
                 "README.md", "AGENT.md", "mcp-config.example.json")
    source_files = [ROOT / name for name in top_level]
    patterns = {"src": ("*.cs",), "bridge": ("*.py",), "tests": ("*.py", "*.cs", "*.csproj", "*.props"),
                "verification": ("*.md", "*.cs", "*.csproj"), "assets": ("**/*.xml", "**/*.csv")}
    for folder, globs in patterns.items():
        for pattern in globs:
            source_files.extend((ROOT / folder).glob(pattern))
    with zipfile.ZipFile(source_archive, "w", zipfile.ZIP_DEFLATED) as z:
        for source in sorted(set(source_files)):
            z.write(source, "ProbablyStolenMcp/" + str(source.relative_to(ROOT)))
    sums = ROOT / "dist" / "SHA256SUMS.txt"
    entries = [archive, source_archive, MOD / "ProbablyStolenMcp.dll"]
    sums.write_text("".join(hashlib.sha256(p.read_bytes()).hexdigest() + "  " + str(p.relative_to(ROOT / "dist")) + "\n" for p in entries), encoding="utf-8")
    print(archive)
    print(sums)


if __name__ == "__main__":
    main()
