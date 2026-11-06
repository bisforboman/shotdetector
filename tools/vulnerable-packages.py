"""The vulnerable NuGet packages in `dotnet list package --vulnerable --include-transitive --format json` listings.

    python tools/vulnerable-packages.py LISTING.json... > report.md

Writes a Markdown table (package, version, severity, advisory, the projects it is in) and exits 1 when any package
has an advisory, 2 when a listing reports a problem (a failed restore) instead, 0 when there is nothing.
vulnerable-packages.yml runs it weekly on main (docs/decisions/2026-11-06-vulnerable-packages.md).
"""

import json
import os
import sys

SEVERITY_ORDER = {"Critical": 0, "High": 1, "Moderate": 2, "Low": 3}


def main(paths):
    found = {}  # (package, version, severity, advisory) -> set of projects
    problems = []
    root = os.getcwd()
    for path in paths:
        with open(path, encoding="utf-8-sig") as f:
            listing = json.load(f)
        for problem in listing.get("problems", []):
            problems.append(f"{path}: {problem.get('level', '?')}: {problem.get('text', '')}")
        for project in listing.get("projects", []):
            name = os.path.relpath(project["path"], root).replace(os.sep, "/")
            for framework in project.get("frameworks", []):
                packages = framework.get("topLevelPackages", []) + framework.get("transitivePackages", [])
                for package in packages:
                    for vulnerability in package.get("vulnerabilities", []):
                        key = (
                            package["id"],
                            package.get("resolvedVersion", "?"),
                            vulnerability.get("severity", "?"),
                            vulnerability.get("advisoryurl", "?"),
                        )
                        found.setdefault(key, set()).add(f"{name} ({framework['framework']})")

    if problems:
        print("The listing could not run:\n")
        for problem in problems:
            print(f"- {problem}")
        print()
    if found:
        print("| Package | Version | Severity | Advisory | Projects |")
        print("|---|---|---|---|---|")
        for (package, version, severity, advisory), projects in sorted(
            found.items(), key=lambda item: (SEVERITY_ORDER.get(item[0][2], 9), item[0][0].lower(), item[0][3])
        ):
            print(f"| {package} | {version} | {severity} | {advisory} | {'<br>'.join(sorted(projects))} |")
        print(
            "\nMove each package to a fixed version in Directory.Packages.props, with a comment naming the advisory (a "
            "transitive one through a direct reference at that version); never suppress the warning."
        )
        return 1
    if problems:
        return 2
    print("No vulnerable packages.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
