"""Report the connector release version and reject version downgrades."""

import argparse
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET


PROJECT = "src/Vespa.NET.VectorData/Vespa.NET.VectorData.csproj"


def read_version(contents):
    version = ET.fromstring(contents).findtext(".//Version")
    if not version or not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", version):
        raise ValueError("The connector release must have an explicit stable major.minor.patch version.")
    return version, tuple(map(int, version.split(".")))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--previous-ref", default="", help="Base commit for a pull request or push")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    version, current = read_version((root / PROJECT).read_text())
    changed = False
    if args.previous_ref:
        previous_xml = subprocess.check_output(
            ["git", "show", f"{args.previous_ref}:{PROJECT}"], cwd=root, text=True
        )
        previous_version, previous = read_version(previous_xml)
        if current < previous:
            raise ValueError(f"Connector version cannot decrease from {previous_version} to {version}.")
        changed = current > previous
        print(f"Connector version: {previous_version} -> {version}")
    output = f"version={version}\nversion_changed={str(changed).lower()}\n"
    print(output, end="")
    if output_path := os.environ.get("GITHUB_OUTPUT"):
        with open(output_path, "a") as stream:
            stream.write(output)


if __name__ == "__main__":
    main()
