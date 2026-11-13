"""Release-layout checks; no game execution and no game assets are copied."""
import hashlib
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MOD_FILES = {"ProbablyStolenMcp.dll", "manifest.xml", "Localization/en.csv", "Localization/zh.csv"}


class PackageTests(unittest.TestCase):
    def test_manifest_is_explicit_and_least_privilege(self):
        root = ET.parse(ROOT / "assets/manifest.xml").getroot()
        self.assertEqual(root.findtext("ID"), "xufly_probably_stolen_mcp")
        self.assertEqual(root.findtext("ModVersion"), "0.1.2")
        self.assertEqual([x.text for x in root.find("Assemblies")], ["ProbablyStolenMcp.dll"])
        self.assertEqual([x.text for x in root.find("Capabilities")], ["network"])
        self.assertEqual([x.text for x in root.find("GameVersions")], ["0.46D"])
        self.assertIsNotNone(root.find("Prerequisites"))

    def test_localization_keys_placeholders_and_no_duplicates(self):
        locales = {}
        for lang in ("en", "zh"):
            values = {}
            for line in (ROOT / f"assets/Localization/{lang}.csv").read_text(encoding="utf-8").splitlines():
                if not line or line.startswith("#"):
                    continue
                key, separator, value = line.partition(",")
                self.assertTrue(separator)
                self.assertNotIn(key, values)
                self.assertTrue(value)
                values[key] = value
            locales[lang] = values
        self.assertEqual(locales["en"].keys(), locales["zh"].keys())
        for key in locales["en"]:
            self.assertEqual(set(re.findall(r"\{\d+\}", locales["en"][key])),
                             set(re.findall(r"\{\d+\}", locales["zh"][key])))

    def test_game_references_never_copy_to_output(self):
        root = ET.parse(ROOT / "ProbablyStolenMcp.csproj").getroot()
        self.assertEqual(root.findtext("PropertyGroup/TargetFramework"), "netstandard2.1")
        self.assertEqual(root.findtext("PropertyGroup/EnableDefaultCompileItems"), "false")
        self.assertEqual([x.attrib["Include"] for x in root.findall("ItemGroup/Compile")], ["src/**/*.cs"])
        for reference in root.findall("ItemGroup/Reference"):
            self.assertEqual(reference.findtext("Private"), "false")

    def test_version_matches_runtime_and_frontend(self):
        from bridge.server import TOOLS
        version = ET.parse(ROOT / "assets/manifest.xml").getroot().findtext("ModVersion")
        self.assertEqual(ET.parse(ROOT / "ProbablyStolenMcp.csproj").getroot().findtext("PropertyGroup/Version"), version)
        source = (ROOT / "src/ModEntry.cs").read_text(encoding="utf-8")
        self.assertIn('public const string Version = "' + version + '"', source)
        self.assertEqual(len(TOOLS), 6)

    def test_port_setting_uses_native_input_not_ranged_slider(self):
        source = (ROOT / "src/ModEntry.cs").read_text(encoding="utf-8")
        definition = next(line for line in source.splitlines() if 'port = config.Define(' in line)
        self.assertNotIn("min:", definition)
        self.assertNotIn("max:", definition)
        self.assertIn("port.OnChanged += PortChanged", source)
        self.assertIn("port.Set(lastValidPort)", source)

    def test_example_never_contains_working_credentials(self):
        import json
        config = json.loads((ROOT / "mcp-config.example.json").read_text())
        settings = config["mcpServers"]["probably-stolen"]
        self.assertEqual(settings["env"]["PS_MCP_TOKEN"], "REPLACE_WITH_YOUR_PRIVATE_TOKEN")
        self.assertNotIn("PS_MCP_HOST", settings["env"])

    @unittest.skipUnless((ROOT / "dist/ProbablyStolenMcp").exists(), "Build release package first")
    def test_clean_mod_folder_exact_allowlist(self):
        mod = ROOT / "dist/ProbablyStolenMcp"
        self.assertEqual({str(p.relative_to(mod)) for p in mod.rglob("*") if p.is_file()}, MOD_FILES)
        self.assertEqual((mod / "ProbablyStolenMcp.dll").read_bytes(),
                         (ROOT / "bin/Release/netstandard2.1/ProbablyStolenMcp.dll").read_bytes())

    @unittest.skipUnless((ROOT / "dist/ProbablyStolenMcp-0.1.2.zip").exists(), "Build release package first")
    def test_archive_and_checksums(self):
        with zipfile.ZipFile(ROOT / "dist/ProbablyStolenMcp-0.1.2.zip") as archive:
            names = set(archive.namelist())
            self.assertEqual({name.removeprefix("Mods/ProbablyStolenMcp/") for name in names if name.startswith("Mods/")}, MOD_FILES)
            self.assertFalse(any(".inspect" in name or "Assembly-CSharp" in name or "__pycache__" in name for name in names))
            self.assertIn("mcp/bridge/server.py", names)
            self.assertIn("README.md", names)
            self.assertIsNone(archive.testzip())
        for line in (ROOT / "dist/SHA256SUMS.txt").read_text().splitlines():
            digest, relative = line.split("  ", 1)
            self.assertEqual(hashlib.sha256((ROOT / "dist" / relative).read_bytes()).hexdigest(), digest)


if __name__ == "__main__":
    unittest.main()
