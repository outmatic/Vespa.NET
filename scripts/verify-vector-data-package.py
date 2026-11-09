"""Verify the connector artifact using published dependencies; never publish it."""

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
    parser.add_argument("packages", type=Path, help="Directory containing only the connector .nupkg")
    args = parser.parse_args()
    packages = args.packages.resolve()
    root = Path(__file__).resolve().parents[1]
    project = root / "src/Vespa.NET.VectorData"
    version = ET.parse(project / "Vespa.NET.VectorData.csproj").findtext(".//Version")
    archives = list(packages.glob("*.nupkg"))
    require(len(archives) == 1, "Expected exactly one .nupkg: pack only Vespa.NET.VectorData into a clean directory.")

    with zipfile.ZipFile(archives[0]) as package:
        nuspecs = [name for name in package.namelist() if name.endswith(".nuspec")]
        require(len(nuspecs) == 1, "Expected one package manifest.")
        manifest = ET.fromstring(package.read(nuspecs[0]))
        for element in manifest.iter():
            element.tag = element.tag.rsplit("}", 1)[-1]
        metadata = manifest.find("metadata")
        require(metadata.findtext("id") == "Vespa.NET.VectorData", "Unexpected package ID.")
        require(metadata.findtext("version") == version, "Package version differs from the project.")
        require(metadata.findtext("license") == "MIT", "Expected the MIT license.")
        require(metadata.findtext("readme") == "README.md", "Package README is missing.")
        require(package.read("README.md") == (project / "README.md").read_bytes(), "Package README is out of date.")
        for framework in ("net8.0", "net10.0"):
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
    # A fresh cache and exact source mapping force the base client to come from nuget.org.
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
        ET.SubElement(central.find("ItemGroup"), "PackageVersion", Include="Vespa.NET.VectorData", Version=version)
        central.write(consumer / "Directory.Packages.props")
        for source in tests.glob("*.cs"):
            if source.name != "LiveVespaTests.cs":
                shutil.copyfile(source, consumer / source.name)

        config = ET.Element("configuration")
        sources = ET.SubElement(config, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="connector", value=str(packages))
        ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        mapping = ET.SubElement(config, "packageSourceMapping")
        ET.SubElement(ET.SubElement(mapping, "packageSource", key="connector"), "package", pattern="Vespa.NET.VectorData")
        ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
        ET.ElementTree(config).write(consumer / "NuGet.Config")
        print(f"Verifying Vespa.NET.VectorData {version} against nuget.org dependencies.", flush=True)
        subprocess.run(["dotnet", "restore", "PackageTests.csproj", "--packages", str(consumer / "packages")],
                       cwd=consumer, check=True, timeout=300)
        # Keep testhost on the same dotnet installation checked above, including portable installations.
        subprocess.run(["dotnet", "test", "PackageTests.csproj", "-c", "Release", "--no-restore", "--disable-build-servers",
                        "--", f"RunConfiguration.DotNetHostPath={shutil.which('dotnet')}", "RunConfiguration.TreatNoTestsAsError=true"],
                       cwd=consumer, check=True, timeout=300)
    print("Package verified on .NET 8 and .NET 10. Nothing was published.")


if __name__ == "__main__":
    main()
