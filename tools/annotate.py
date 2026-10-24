"""GitHub Actions workflow commands for the comparison tools (compare.py, realworld.py): an annotation on the check
run, which `gh api repos/<owner>/<repo>/check-runs/<id>/annotations` reads where job logs and step summaries can't be.
Prints nothing outside GitHub Actions. GitHub keeps 10 errors and 10 warnings per step, 50 annotations per job.

Usage: python tools/annotate.py --test   (the self-test)"""
import os
import sys


def escape_data(s: str) -> str:
    """A workflow command's message: % and line breaks escaped (one line)."""
    return s.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(s: str) -> str:
    """A workflow command's property value (title=...): the message's escapes and also : and ,."""
    return escape_data(s).replace(":", "%3A").replace(",", "%2C")


def command(kind: str, message: str, title: str | None = None) -> str:
    """`::error title=...::message` (kind error, warning or notice)."""
    return f"::{kind}{' title=' + escape_property(title) if title else ''}::{escape_data(message)}"


def emit(kind: str, message: str, title: str | None = None) -> None:
    if os.environ.get("GITHUB_ACTIONS") == "true":
        print(command(kind, message, title), flush=True)


def _test() -> None:
    assert escape_data("50% a\r\nb") == "50%25 a%0D%0Ab"
    assert escape_property("x: a, b%") == "x%3A a%2C b%25"
    assert command("notice", "all matched") == "::notice::all matched"
    assert command("error", "a: 1\nb", "compare.py: x.mp4, y") == "::error title=compare.py%3A x.mp4%2C y::a: 1%0Ab"
    # compare.py's: one error per sample (not per detector), the first difference, then the notice.
    import compare
    row = dict(video="a.mp4", detector="content", missing=[], extra=[], csv_cells=0, stat_diffs={"content_val": 0})
    rows = [row, {**row, "detector": "hist", "csv_cells": 2, "first": "scene list line 3 End Frame: scenedetect 1, ours 2"},
            {**row, "detector": "adaptive", "missing": [120], "csv_cells": 4, "first": "cut 120 only in scenedetect"}]
    assert compare.annotations(rows, "compare.py", False, "") == [
        ("error", "compare.py: a.mp4 differs", "a.mp4: hist: 2 scene list cells; adaptive: 1 cuts only in scenedetect, "
         "4 scene list cells. First (hist): scene list line 3 End Frame: scenedetect 1, ours 2"),
        ("notice", "compare.py: 1 of 3 matched", "1 of 3 comparisons matched (1 samples); 1 samples differ")]
    assert compare.annotations(rows, "compare.py", True, "--fast-yuv") == [
        ("notice", "compare.py (informational): 1 of 3 matched",
         "1 of 3 comparisons matched (1 samples) (--fast-yuv). Differ: a.mp4 (hist, adaptive)")]
    many = compare.annotations([{**rows[1], "video": f"v{i}.mp4"} for i in range(12)], "x", False, "")
    assert [k for k, _, _ in many] == ["error"] * 10 + ["notice"] and many[9][2] == "v9.mp4, v10.mp4, v11.mp4"
    assert compare.first_csv_difference([["Scene Number", "Start Frame"], ["1", "1"]],
                                        [["Scene Number", "Start Frame"], ["1", "2"], ["2"]]) \
        == "scene list line 2 Start Frame: scenedetect 1, ours 2"
    print("annotate.py: ok")


if __name__ == "__main__":
    if sys.argv[1:] != ["--test"]:
        sys.exit(__doc__)
    _test()
