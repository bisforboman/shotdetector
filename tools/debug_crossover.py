"""Time shotdetect --decoder process with whole frames (SD_FORCE=full) against remap's rows (SD_FORCE=sampled),
interleaved, alternating which goes first. Usage: crossover.py <exe> <rounds> <video>...
Then, with FASTYUV=<exe of the LGPL build> and BBB=<video>: process vs process + --fast-yuv, default and --sequential."""
import os
import resource
import statistics
import subprocess
import sys
import time


def run(cmd, env_extra):
    env = dict(os.environ, **env_extra)
    r0 = resource.getrusage(resource.RUSAGE_CHILDREN)
    t0 = time.perf_counter()
    p = subprocess.run(cmd, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    wall = time.perf_counter() - t0
    r1 = resource.getrusage(resource.RUSAGE_CHILDREN)
    cpu = (r1.ru_utime - r0.ru_utime) + (r1.ru_stime - r0.ru_stime)
    if p.returncode != 0:
        sys.exit(f"{cmd} failed: {p.stderr[-2000:]}")
    info = [l for l in p.stderr.splitlines() if l.startswith("DEBUG")]
    return wall, cpu, p.stdout, info[0] if info else "?"


def compare(label, a, b, rounds):
    """a, b: (name, cmd, env). Medians of wall and CPU; ratio b/a."""
    res = {a[0]: ([], []), b[0]: ([], [])}
    outs, infos = {}, {}
    for r in range(rounds):
        for name, cmd, env in ((a, b) if r % 2 == 0 else (b, a)):
            w, c, out, info = run(cmd, env)
            res[name][0].append(w)
            res[name][1].append(c)
            outs[name], infos[name] = out, info
    same = "same" if outs[a[0]] == outs[b[0]] else "DIFFERENT OUTPUT"
    wa, ca = statistics.median(res[a[0]][0]), statistics.median(res[a[0]][1])
    wb, cb = statistics.median(res[b[0]][0]), statistics.median(res[b[0]][1])
    line = (f"| {label} | {wa:.2f} | {wb:.2f} | {wb / wa:.3f} | {ca:.2f} | {cb:.2f} | {cb / ca:.3f} | {same} "
            f"| {infos[a[0]]}; {infos[b[0]]} | {' '.join(f'{x:.2f}' for x in res[a[0]][0])} / {' '.join(f'{x:.2f}' for x in res[b[0]][0])} |")
    print(line, flush=True)
    return line


def main():
    exe, rounds, videos = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
    lines = ["| video | wall full | wall sampled | sampled/full | CPU full | CPU sampled | sampled/full | output | pipelines | walls |",
             "|---|---|---|---|---|---|---|---|---|---|"]
    print("\n".join(lines))
    base = ["--decoder", "process", "--sequential", "-d", "content"]
    for v in videos:
        cmd = [exe, "-i", v, *base]
        lines.append(compare(os.path.basename(v), ("full", cmd, {"SD_FORCE": "full"}),
                             ("sampled", cmd, {"SD_FORCE": "sampled"}), rounds))
    fast, bbb = os.environ.get("FASTYUV"), os.environ.get("BBB")
    if fast and bbb:
        lines += ["", "| BBB 640x360 | wall process | wall +fast-yuv | ratio | CPU process | CPU +fast-yuv | ratio | output | pipelines | walls |",
                  "|---|---|---|---|---|---|---|---|---|---|"]
        print("\n".join(lines[-2:]))
        for extra in ([], ["--sequential"]):
            lines.append(compare("default" if not extra else "--sequential",
                                 ("process", [fast, "-i", bbb, "--decoder", "process", *extra], {}),
                                 ("fastyuv", [fast, "-i", bbb, "--decoder", "process", "--fast-yuv", *extra], {}), rounds))
    with open(os.environ.get("GITHUB_STEP_SUMMARY", os.devnull), "a") as f:
        f.write("\n".join(lines) + "\n")


main()
