"""Shared package inspection and isolated restore for adapter verification."""

import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
import zipfile


CORE_PACKAGES = ("Vespa.NET.Models", "Vespa.NET", "Vespa.NET.Testcontainers")
FRAMEWORKS = ("net8.0", "net10.0")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_feed(feed, package_id, candidate):
    archives = sorted(feed.glob("*.nupkg"))
    if not candidate:
        require(len(archives) == 1, f"Pack only {package_id} into a clean directory.")
    packages = {}
    for path in archives:
        with zipfile.ZipFile(path) as archive:
            manifests = [name for name in archive.namelist() if name.endswith(".nuspec")]
            require(len(manifests) == 1, f"Expected one package manifest in {path.name}.")
            manifest = ET.fromstring(archive.read(manifests[0]))
        for element in manifest.iter():
            element.tag = element.tag.rsplit("}", 1)[-1]
        metadata = manifest.find("metadata")
        name = metadata.findtext("id")
        require(name not in packages, f"Expected a single version of {name} in {feed}.")
        packages[name] = (path, metadata)
    require(package_id in packages, f"Missing {package_id} in {feed}.")
    if candidate:
        for name in CORE_PACKAGES:
            require(name in packages, f"Candidate feed is missing {name}.")
        versions = {packages[name][1].findtext("version") for name in CORE_PACKAGES}
        require(len(versions) == 1, "Core candidate packages must share a version.")
    return packages


def restore_consumer(consumer, cache, feed, local_packages):
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="local", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    local = ET.SubElement(mapping, "packageSource", key="local")
    for name in local_packages:
        ET.SubElement(local, "package", pattern=name)
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
    ET.ElementTree(config).write(consumer / "NuGet.Config")
    subprocess.run(
        ["dotnet", "restore", "PackageTests.csproj", "--configfile", "NuGet.Config", "--packages", str(cache)],
        cwd=consumer, check=True, timeout=300,
    )


def verify_candidate_restore(consumer, cache, feed, packages, package_ids):
    assets = json.loads((consumer / "obj/project.assets.json").read_text())
    for framework in FRAMEWORKS:
        targets = assets["targets"][framework]
        for name in package_ids:
            version = packages[name][1].findtext("version")
            identity = f"{name}/{version}"
            require(targets.get(identity, {}).get("type") == "package", f"{framework} did not resolve {identity}.")
            metadata = json.loads((cache / name.lower() / version / ".nupkg.metadata").read_text())
            require(Path(metadata["source"]).resolve() == feed, f"{identity} was restored from another feed.")
            print(f"{framework} {identity} (local candidate)", flush=True)
