"""Verify the Agent Framework integration against published dependencies; never publish it."""

import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile


PACKAGE_TEST_FILES = (
    "CompactionTests.cs",
    "FileSearchTests.cs",
    "HistoryReliabilityTests.cs",
    "PatternTests.cs",
)

TEST_DEPENDENCIES = (
    "Microsoft.NET.Test.Sdk",
    "Microsoft.Agents.AI.Harness",
    "xunit",
    "xunit.runner.visualstudio",
)


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
    versions = {item.get("Include"): item.get("Version")
                for item in ET.parse(root / "Directory.Packages.props").iter("PackageVersion")}
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
        globbing_version = versions["Microsoft.Extensions.FileSystemGlobbing"]
        for group in groups:
            dependencies = {d.get("id"): d.get("version") for d in group.findall("dependency")}
            require(set(dependencies) == {"Vespa.NET", "Microsoft.Agents.AI", "Microsoft.Extensions.FileSystemGlobbing"},
                    "Unexpected package dependencies.")
            require(dependencies["Microsoft.Extensions.FileSystemGlobbing"] == globbing_version,
                    "Unexpected file-system globbing dependency version.")
    runtimes = subprocess.check_output(["dotnet", "--list-runtimes"], text=True, timeout=30)
    require(all(f"Microsoft.NETCore.App {major}." in runtimes for major in (8, 10)), "Install .NET 8 and .NET 10 runtimes.")
    with tempfile.TemporaryDirectory(prefix="vespa-agent-package-") as temporary:
        workspace = Path(temporary)
        consumer = workspace / "consumer"
        consumer.mkdir()
        tests = root / "tests/Vespa.NET.AgentFramework.Tests"
        # The package consumer compiles only reusable contract tests. The
        # repository test project also references samples and benchmark sources.
        test_project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        properties = ET.SubElement(test_project, "PropertyGroup")
        ET.SubElement(properties, "TargetFrameworks").text = "net8.0;net10.0"
        ET.SubElement(properties, "LangVersion").text = "latest"
        ET.SubElement(properties, "ImplicitUsings").text = "enable"
        ET.SubElement(properties, "Nullable").text = "enable"
        ET.SubElement(properties, "IsPackable").text = "false"
        references = ET.SubElement(test_project, "ItemGroup")
        ET.SubElement(references, "PackageReference", Include="Vespa.NET.AgentFramework", Version=f"[{version}]")
        for dependency in TEST_DEPENDENCIES:
            reference = ET.SubElement(references, "PackageReference", Include=dependency, Version=versions[dependency])
            if dependency == "xunit.runner.visualstudio":
                reference.set("PrivateAssets", "all")
        ET.ElementTree(test_project).write(consumer / "PackageTests.csproj")
        for filename in PACKAGE_TEST_FILES:
            shutil.copyfile(tests / filename, consumer / filename)
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
        subprocess.run(["dotnet", "restore", "PackageTests.csproj", "--configfile", "NuGet.Config",
                        "--packages", str(workspace / "packages")], cwd=consumer, check=True, timeout=300)
        for framework in ("net8.0", "net10.0"):
            subprocess.run(["dotnet", "test", "PackageTests.csproj", "-c", "Release", "-f", framework,
                            "--no-restore", "--disable-build-servers", "--",
                            f"RunConfiguration.DotNetHostPath={shutil.which('dotnet')}",
                            "RunConfiguration.TreatNoTestsAsError=true"], cwd=consumer, check=True, timeout=300)
    print("Agent Framework package verified on .NET 8 and .NET 10. Nothing was published.")


if __name__ == "__main__":
    main()
