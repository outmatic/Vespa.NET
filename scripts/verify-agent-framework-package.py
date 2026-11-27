"""Verify the Agent Framework artifact against published or local candidate dependencies."""

import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

from package_verification import FRAMEWORKS, read_feed, require, restore_consumer, verify_candidate_restore


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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", type=Path)
    parser.add_argument("--candidate", action="store_true", help="Use the local feed for the core dependencies too")
    args = parser.parse_args()
    packages = args.packages.resolve()
    root = Path(__file__).resolve().parents[1]
    project = root / "src/Vespa.NET.AgentFramework"
    package_id = "Vespa.NET.AgentFramework"
    feed = read_feed(packages, package_id, args.candidate)
    path, metadata = feed[package_id]
    version = metadata.findtext("version") if args.candidate else ET.parse(project / f"{package_id}.csproj").findtext(".//Version")
    versions = {item.get("Include"): item.get("Version")
                for item in ET.parse(root / "Directory.Packages.props").iter("PackageVersion")}
    with zipfile.ZipFile(path) as archive:
        require(metadata.findtext("version") == version, "Unexpected package version.")
        require(metadata.findtext("license") == "MIT", "Expected the MIT license.")
        require(metadata.findtext("readme") == "README.md", "Expected the package README.")
        require(archive.read("README.md") == (project / "README.md").read_bytes(), "Embedded README is out of date.")
        for framework in FRAMEWORKS:
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
        ET.SubElement(references, "PackageReference", Include=package_id, Version=f"[{version}]")
        for dependency in TEST_DEPENDENCIES:
            reference = ET.SubElement(references, "PackageReference", Include=dependency, Version=versions[dependency])
            if dependency == "xunit.runner.visualstudio":
                reference.set("PrivateAssets", "all")
        ET.ElementTree(test_project).write(consumer / "PackageTests.csproj")
        for filename in PACKAGE_TEST_FILES:
            shutil.copyfile(tests / filename, consumer / filename)
        dependency_source = "the local core candidate" if args.candidate else "nuget.org dependencies"
        print(f"Verifying {package_id} {version} against {dependency_source}.", flush=True)
        cache = workspace / "packages"
        candidate_ids = (package_id, "Vespa.NET", "Vespa.NET.Models")
        restore_consumer(consumer, cache, packages, candidate_ids if args.candidate else (package_id,))
        if args.candidate:
            verify_candidate_restore(consumer, cache, packages, feed, candidate_ids)
        for framework in FRAMEWORKS:
            subprocess.run(["dotnet", "test", "PackageTests.csproj", "-c", "Release", "-f", framework,
                            "--no-restore", "--disable-build-servers", "--",
                            f"RunConfiguration.DotNetHostPath={shutil.which('dotnet')}",
                            "RunConfiguration.TreatNoTestsAsError=true"], cwd=consumer, check=True, timeout=300)
    print("Agent Framework package verified on .NET 8 and .NET 10. Nothing was published.")


if __name__ == "__main__":
    main()
