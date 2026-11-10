"""Verify the Agent Framework integration against published dependencies; never publish it."""

import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile


def require(condition, message):
    if not condition:
        raise ValueError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", type=Path)
    packages = parser.parse_args().packages.resolve()
    root = Path(__file__).resolve().parents[1]
    project = root / "src/Vespa.NET.AgentFramework"
    version = ET.parse(project / "Vespa.NET.AgentFramework.csproj").findtext(".//Version")
    archives = list(packages.glob("*.nupkg"))
    require(len(archives) == 1, "Expected only the Agent Framework integration package in a clean directory.")
    with zipfile.ZipFile(archives[0]) as archive:
        manifests = [name for name in archive.namelist() if name.endswith(".nuspec")]
        require(len(manifests) == 1, "Expected one package manifest.")
        manifest = ET.fromstring(archive.read(manifests[0]))
        for element in manifest.iter():
            element.tag = element.tag.rsplit("}", 1)[-1]
        metadata = manifest.find("metadata")
        require(metadata.findtext("id") == "Vespa.NET.AgentFramework", "Unexpected package ID.")
        require(metadata.findtext("version") == version, "Unexpected package version.")
        require(metadata.findtext("license") == "MIT", "Expected the MIT license.")
        require(metadata.findtext("readme") == "README.md", "Expected the package README.")
        require(archive.read("README.md") == (project / "README.md").read_bytes(), "Embedded README is out of date.")
        for framework in ("net8.0", "net10.0"):
            for extension in ("dll", "xml"):
                require(f"lib/{framework}/Vespa.NET.AgentFramework.{extension}" in archive.namelist(), "Missing assembly or API documentation.")
        groups = metadata.findall("dependencies/group")
        require(len(groups) == 2, "Expected two target framework groups.")
        for group in groups:
            require({d.get("id") for d in group.findall("dependency")} == {"Vespa.NET", "Microsoft.Agents.AI"}, "Unexpected package dependencies.")
    runtimes = subprocess.check_output(["dotnet", "--list-runtimes"], text=True, timeout=30)
    require(all(f"Microsoft.NETCore.App {major}." in runtimes for major in (8, 10)), "Install .NET 8 and .NET 10 runtimes.")
    with tempfile.TemporaryDirectory(prefix="vespa-agent-package-") as temporary:
        consumer = Path(temporary)
        tests = root / "tests/Vespa.NET.AgentFramework.Tests"
        tree = ET.parse(tests / "Vespa.NET.AgentFramework.Tests.csproj")
        properties = tree.find("PropertyGroup")
        properties.remove(properties.find("TargetFramework"))
        ET.SubElement(properties, "TargetFrameworks").text = "net8.0;net10.0"
        ET.SubElement(properties, "LangVersion").text = "latest"
        for group in tree.findall("ItemGroup"):
            for item in list(group):
                if item.tag in ("ProjectReference", "None"):
                    group.remove(item)
        ET.SubElement(tree.find("ItemGroup"), "PackageReference", Include="Vespa.NET.AgentFramework")
        tree.write(consumer / "PackageTests.csproj")
        central = ET.parse(root / "Directory.Packages.props")
        ET.SubElement(central.find("ItemGroup"), "PackageVersion", Include="Vespa.NET.AgentFramework", Version=version)
        central.write(consumer / "Directory.Packages.props")
        for source in tests.glob("*.cs"):
            if source.name not in ("LiveVespaTests.cs", "SamplePatternTests.cs"):
                shutil.copyfile(source, consumer / source.name)
        config = ET.Element("configuration")
        sources = ET.SubElement(config, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="integration", value=str(packages))
        ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        mapping = ET.SubElement(config, "packageSourceMapping")
        ET.SubElement(ET.SubElement(mapping, "packageSource", key="integration"), "package", pattern="Vespa.NET.AgentFramework")
        ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
        ET.ElementTree(config).write(consumer / "NuGet.Config")
        print(f"Verifying Vespa.NET.AgentFramework {version} against nuget.org dependencies.", flush=True)
        subprocess.run(["dotnet", "restore", "PackageTests.csproj", "--packages", str(consumer / "packages")], cwd=consumer, check=True, timeout=300)
        subprocess.run(["dotnet", "test", "PackageTests.csproj", "-c", "Release", "--no-restore", "--disable-build-servers",
                        "--", f"RunConfiguration.DotNetHostPath={shutil.which('dotnet')}", "RunConfiguration.TreatNoTestsAsError=true"],
                       cwd=consumer, check=True, timeout=300)
    print("Agent Framework package verified on .NET 8 and .NET 10. Nothing was published.")


if __name__ == "__main__":
    main()
