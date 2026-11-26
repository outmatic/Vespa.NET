"""Verify the connector artifact against published or local candidate dependencies."""

import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

from package_verification import FRAMEWORKS, read_feed, require, restore_consumer, verify_candidate_restore


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", type=Path, help="Directory containing the connector .nupkg")
    parser.add_argument("--candidate", action="store_true", help="Use the local feed for the core dependencies too")
    args = parser.parse_args()
    packages = args.packages.resolve()
    root = Path(__file__).resolve().parents[1]
    project = root / "src/Vespa.NET.VectorData"
    package_id = "Vespa.NET.VectorData"
    feed = read_feed(packages, package_id, args.candidate)
    archive, metadata = feed[package_id]
    version = metadata.findtext("version") if args.candidate else ET.parse(project / f"{package_id}.csproj").findtext(".//Version")

    with zipfile.ZipFile(archive) as package:
        require(metadata.findtext("version") == version, "Package version differs from the project.")
        require(metadata.findtext("license") == "MIT", "Expected the MIT license.")
        require(metadata.findtext("readme") == "README.md", "Package README is missing.")
        require(package.read("README.md") == (project / "README.md").read_bytes(), "Package README is out of date.")
        for framework in FRAMEWORKS:
            for extension in ("dll", "xml"):
                require(f"lib/{framework}/Vespa.NET.VectorData.{extension}" in package.namelist(),
                        f"Missing {framework} assembly or API documentation.")
        groups = metadata.findall("dependencies/group")
        require(len(groups) == 2, "Expected dependency groups for both target frameworks.")
        for group in groups:
            dependencies = {item.get("id") for item in group.findall("dependency")}
            require(dependencies == {"Vespa.NET", "Microsoft.Extensions.VectorData.Abstractions"},
                    f"Unexpected public dependencies: {dependencies}")

    # Reuse the existing behavior tests with package references instead of projects.
    # A fresh cache applies the source mapping even if the package was installed before.
    runtimes = subprocess.check_output(["dotnet", "--list-runtimes"], text=True, timeout=30)
    require(all(f"Microsoft.NETCore.App {major}." in runtimes for major in (8, 10)),
            "Install both the .NET 8 and .NET 10 runtimes to execute the package tests.")
    with tempfile.TemporaryDirectory(prefix="vespa-vector-package-") as temporary:
        consumer = Path(temporary)
        tests = root / "tests/Vespa.NET.VectorData.Tests"
        tree = ET.parse(tests / "Vespa.NET.VectorData.Tests.csproj")
        properties = tree.find("PropertyGroup")
        properties.remove(properties.find("TargetFramework"))
        ET.SubElement(properties, "TargetFrameworks").text = "net8.0;net10.0"
        ET.SubElement(properties, "LangVersion").text = "latest"
        for group in tree.findall("ItemGroup"):
            for item in list(group):
                if item.tag in ("ProjectReference", "None"):
                    group.remove(item)
                elif item.tag == "Compile" and "Include" in item.attrib:
                    source = (tests / item.get("Include")).resolve()
                    require(source.is_file(), f"Linked test source is missing: {source}")
                    item.set("Include", str(source))
        ET.SubElement(tree.find("ItemGroup"), "PackageReference", Include="Vespa.NET.VectorData")
        tree.write(consumer / "PackageTests.csproj")
        central = ET.parse(root / "Directory.Packages.props")
        ET.SubElement(central.find("ItemGroup"), "PackageVersion", Include=package_id, Version=f"[{version}]")
        central.write(consumer / "Directory.Packages.props")
        for source in tests.glob("*.cs"):
            if source.name not in {"LiveVespaTests.cs", "LiveFeatureTests.cs"}:
                shutil.copyfile(source, consumer / source.name)

        dependency_source = "the local core candidate" if args.candidate else "nuget.org dependencies"
        print(f"Verifying {package_id} {version} against {dependency_source}.", flush=True)
        cache = consumer / "packages"
        candidate_ids = (package_id, "Vespa.NET", "Vespa.NET.Models")
        restore_consumer(consumer, cache, packages, candidate_ids if args.candidate else (package_id,))
        if args.candidate:
            verify_candidate_restore(consumer, cache, packages, feed, candidate_ids)
        # Keep testhost on the same dotnet installation checked above, including portable installations.
        subprocess.run(["dotnet", "test", "PackageTests.csproj", "-c", "Release", "--no-restore", "--disable-build-servers",
                        "--", f"RunConfiguration.DotNetHostPath={shutil.which('dotnet')}", "RunConfiguration.TreatNoTestsAsError=true"],
                       cwd=consumer, check=True, timeout=300)
    print("Package verified on .NET 8 and .NET 10. Nothing was published.")


if __name__ == "__main__":
    main()
